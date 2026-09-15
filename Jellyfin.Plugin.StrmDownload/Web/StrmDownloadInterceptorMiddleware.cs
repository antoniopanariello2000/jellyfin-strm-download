using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;
using HeaderUtilities = Microsoft.Net.Http.Headers.HeaderUtilities;

namespace Jellyfin.Plugin.StrmDownload.Web;

/// <summary>
/// Intercepts the native "Items/{itemId}/Download" request, before it reaches
/// Jellyfin's own controller, and serves the remote content a .strm file
/// points to instead of the .strm text file itself. This fixes downloads for
/// every client (Jellyfin Web, mobile apps, etc.) since they all call the
/// same native URL - no client-side changes needed. GET and HEAD are both
/// intercepted, so a client that sizes the download up front sees the real
/// media's length rather than the .strm file's. Non-.strm items and
/// missing items are left untouched and fall through to Jellyfin's own
/// controller, which handles them exactly as before. Authentication and the
/// user's download permission are checked here, because this middleware runs
/// before ASP.NET Core's authorization middleware.
/// </summary>
public class StrmDownloadInterceptorMiddleware
{
    /// <summary>
    /// Buffer size used while proxying the remote body. Matches the default
    /// of <see cref="Stream.CopyToAsync(Stream)"/>.
    /// </summary>
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Conditional range request headers forwarded to the upstream server.
    /// Range and If-Range belong together: If-Range lets the upstream fall back
    /// to a full 200 response when the validator no longer matches, which is
    /// what makes resuming against a changed file safe.
    /// </summary>
    private static readonly string[] ForwardedRequestHeaders = [HeaderNames.Range, HeaderNames.IfRange];

    private static readonly Regex DownloadPathRegex = new(
        @"/Items/(?<id>[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})/Download/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly ILogger<StrmDownloadInterceptorMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmDownloadInterceptorMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{StrmDownloadInterceptorMiddleware}"/> interface.</param>
    public StrmDownloadInterceptorMiddleware(RequestDelegate next, ILogger<StrmDownloadInterceptorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware. Extra parameters are resolved per-request from
    /// the DI container, as is standard for ASP.NET Core middleware.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ILibraryManager libraryManager,
        IAuthorizationContext authContext,
        IHttpClientFactory httpClientFactory)
    {
        if (Plugin.Instance is null
            || !Plugin.Instance.Configuration.EnableNativeDownloadHook
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var match = DownloadPathRegex.Match(context.Request.Path.Value ?? string.Empty);
        if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var itemId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            // This middleware runs in front of ASP.NET Core's authentication and
            // authorization middleware, so the [Authorize(Policy = Policies.Download)]
            // attribute on Jellyfin's own LibraryController.GetDownload never gets a
            // chance to run for requests we intercept. IAuthorizationContext does not
            // throw for a missing or unknown token - it returns an AuthorizationInfo
            // with IsAuthenticated == false - so the result has to be checked here
            // explicitly, before the item is ever looked up.
            var authInfo = await authContext.GetAuthorizationInfo(context).ConfigureAwait(false);
            if (!authInfo.IsAuthenticated)
            {
                // Mirror what Jellyfin answers natively on this route without a
                // valid token. Deliberately not falling through to _next: doing so
                // would leak whether an item exists and is a .strm file by way of
                // differing responses.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var user = authInfo.User;
            if (user is null && !authInfo.IsApiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // user is null && authInfo.IsApiKey: an API key request has no user
            // context. Jellyfin's controller falls back to item.CanDownload()
            // without a user in that case, and so do we (below).
            var item = libraryManager.GetItemById<BaseItem>(itemId, user);
            if (item is null || !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
            {
                // Not a .strm item (or not found): let Jellyfin's own controller
                // handle it exactly as it always has.
                await _next(context).ConfigureAwait(false);
                return;
            }

            var canDownload = user is not null ? item.CanDownload(user) : item.CanDownload();
            if (!canDownload)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await ProxyStrmDownloadAsync(item, context, httpClientFactory).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to serve .strm download for item {ItemId}", itemId);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private async Task ProxyStrmDownloadAsync(BaseItem item, HttpContext context, IHttpClientFactory httpClientFactory)
    {
        var cancellationToken = context.RequestAborted;
        var strmContent = await System.IO.File.ReadAllTextAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var remoteUrl = strmContent.Trim();
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri))
        {
            _logger.LogError("The .strm file {Path} does not contain a valid absolute URL", item.Path);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        var isHeadRequest = HttpMethods.IsHead(context.Request.Method);
        var httpClient = httpClientFactory.CreateClient();

        var upstream = await SendUpstreamAsync(httpClient, context, remoteUri, isHeadRequest, cancellationToken).ConfigureAwait(false);
        using var upstreamResponse = upstream.Response;
        var probedLength = upstream.ProbedLength;

        var response = context.Response;
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            // Pass the upstream status through unchanged. 416 in particular has
            // to keep its "bytes */<total>" Content-Range, since that is what
            // lets a client correct an unsatisfiable range (RFC 9110 15.5.17).
            response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyRangeHeaders(upstreamResponse, response);
            return;
        }

        // Both 200 and 206 pass through as they are. A 206 without Content-Range
        // is an incomplete response per RFC 9110 15.3.7 and breaks resume. The
        // probe path is the exception: its 206 describes the one probe byte, not
        // the answer this HEAD is owed, so it is rewritten to a plain 200.
        response.StatusCode = probedLength.HasValue ? StatusCodes.Status200OK : (int)upstreamResponse.StatusCode;
        response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (probedLength.HasValue)
        {
            response.ContentLength = probedLength;

            // The probe proved the upstream honours byte ranges even though it
            // refuses HEAD; its own Content-Range is not forwarded.
            response.Headers.AcceptRanges = "bytes";
        }
        else
        {
            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
            {
                response.ContentLength = upstreamResponse.Content.Headers.ContentLength;
            }

            CopyRangeHeaders(upstreamResponse, response);
        }

        CopyValidatorHeaders(upstreamResponse, response);

        var filename = BuildDownloadFilename(item, remoteUri);
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = filename
        }.ToString();

        if (isHeadRequest)
        {
            // A HEAD response carries the same headers as the GET would, but no
            // body (RFC 9110 9.3.2). Kestrel suppresses the body for HEAD and
            // skips its Content-Length verification for it, so the header set
            // above reaches the client as-is.
            return;
        }

        await using var remoteStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await CopyWithIdleTimeoutAsync(remoteStream, context, remoteUri).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues the upstream request that matches the client's method, with a
    /// fallback for upstream servers that do not implement HEAD.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="remoteUri">The URL read from the .strm file.</param>
    /// <param name="isHeadRequest">Whether the client asked for HEAD.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The upstream response and, when the HEAD fallback was used, the total
    /// length derived from the probe's Content-Range.
    /// </returns>
    private async Task<(HttpResponseMessage Response, long? ProbedLength)> SendUpstreamAsync(
        HttpClient httpClient,
        HttpContext context,
        Uri remoteUri,
        bool isHeadRequest,
        CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(isHeadRequest ? HttpMethod.Head : HttpMethod.Get, remoteUri);
        ForwardRequestHeaders(context.Request, requestMessage);

        var responseMessage = await httpClient
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!isHeadRequest
            || (responseMessage.StatusCode != HttpStatusCode.MethodNotAllowed
                && responseMessage.StatusCode != HttpStatusCode.NotImplemented))
        {
            return (responseMessage, null);
        }

        // Fallback: the upstream rejects HEAD (405/501), which plain file
        // servers and some streaming backends do. Ask for a single byte
        // instead and take the total size from the 206's Content-Range, so a
        // client sizing the download up front still sees the true length
        // rather than the 172 bytes of the .strm file.
        //
        // The probe deliberately overrides any Range the client sent: a HEAD
        // carrying a Range is answered with the full length here, not with the
        // range's length. That trade is accepted - HEAD with Range is rare and
        // this path only runs when the upstream is already non-compliant.
        _logger.LogDebug(
            "Upstream {RemoteUri} answered {StatusCode} to HEAD, probing the length with a ranged GET instead",
            remoteUri,
            (int)responseMessage.StatusCode);
        responseMessage.Dispose();

        using var probeRequestMessage = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        probeRequestMessage.Headers.TryAddWithoutValidation(HeaderNames.Range, "bytes=0-0");

        var probeResponseMessage = await httpClient
            .SendAsync(probeRequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var probedLength = probeResponseMessage.Content.Headers.ContentRange?.Length;
        if (probeResponseMessage.IsSuccessStatusCode && probedLength.HasValue)
        {
            return (probeResponseMessage, probedLength);
        }

        // The upstream answered neither HEAD nor a ranged GET usefully; hand the
        // probe's status back so the client sees the upstream's own verdict.
        return (probeResponseMessage, null);
    }

    /// <summary>
    /// Copies the upstream body to the client, enforcing an <em>idle</em>
    /// timeout instead of a total one.
    /// <para>
    /// With <see cref="HttpCompletionOption.ResponseHeadersRead"/> the
    /// <see cref="HttpClient.Timeout"/> only covers the response headers: the
    /// runtime disposes the timeout token source in FinishSend before the body
    /// is read, and only buffers when ResponseContentRead is requested. So
    /// without an explicit timeout here a stalled upstream would pin this
    /// request, its socket and the upstream connection indefinitely.
    /// </para>
    /// <para>
    /// A total timeout is deliberately not used - it would kill long but
    /// perfectly healthy downloads. Instead the timer is armed before each
    /// read and disarmed again as soon as bytes arrive.
    /// </para>
    /// </summary>
    /// <param name="source">The upstream response body.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="remoteUri">The remote URI, used for logging only.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task CopyWithIdleTimeoutAsync(Stream source, HttpContext context, Uri remoteUri)
    {
        var configuredSeconds = Plugin.Instance?.Configuration.StreamIdleTimeoutSeconds ?? 0;
        var idleTimeout = configuredSeconds > 0
            ? TimeSpan.FromSeconds(configuredSeconds)
            : Timeout.InfiniteTimeSpan;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                // Arm the idle timer for this read only.
                cts.CancelAfter(idleTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cts.Token).ConfigureAwait(false);

                // Progress: disarm the timer again so the following write - and
                // the client's pace - cannot trip it.
                cts.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0)
                {
                    break;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The linked source fired but the client is still there, so this is
            // our idle timeout rather than a client abort.
            _logger.LogWarning(
                "Upstream {RemoteUri} stalled for more than {IdleTimeoutSeconds}s, aborting the .strm download",
                remoteUri,
                configuredSeconds);

            if (context.Response.HasStarted)
            {
                // Headers and part of the body are already on the wire; the only
                // way to signal the truncation is to drop the connection.
                context.Abort();
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Forwards the client's conditional range headers to the upstream server,
    /// so that range and resume semantics are decided by the origin and not
    /// silently dropped by this proxy.
    /// </summary>
    /// <param name="request">The incoming client request.</param>
    /// <param name="requestMessage">The outgoing upstream request.</param>
    private static void ForwardRequestHeaders(HttpRequest request, HttpRequestMessage requestMessage)
    {
        foreach (var headerName in ForwardedRequestHeaders)
        {
            if (request.Headers.TryGetValue(headerName, out var value) && value.Count > 0)
            {
                requestMessage.Headers.TryAddWithoutValidation(headerName, (string?)value);
            }
        }
    }

    /// <summary>
    /// Copies the range related headers of the upstream response back to the
    /// client. Applied to error responses too, because a 416 is only actionable
    /// for the client when it carries Content-Range.
    /// </summary>
    /// <param name="responseMessage">The upstream response.</param>
    /// <param name="response">The client response.</param>
    private static void CopyRangeHeaders(HttpResponseMessage responseMessage, HttpResponse response)
    {
        if (responseMessage.Headers.AcceptRanges.Count > 0)
        {
            response.Headers.AcceptRanges = string.Join(", ", responseMessage.Headers.AcceptRanges);
        }

        var contentRange = responseMessage.Content.Headers.ContentRange;
        if (contentRange is not null)
        {
            response.Headers.ContentRange = contentRange.ToString();
        }
    }

    /// <summary>
    /// Copies the cache validators and Vary from the upstream response, when it
    /// provides them, so a client can issue a matching If-Range on resume.
    /// </summary>
    /// <param name="responseMessage">The upstream response.</param>
    /// <param name="response">The client response.</param>
    private static void CopyValidatorHeaders(HttpResponseMessage responseMessage, HttpResponse response)
    {
        var etag = responseMessage.Headers.ETag;
        if (etag is not null)
        {
            response.Headers.ETag = etag.ToString();
        }

        var lastModified = responseMessage.Content.Headers.LastModified;
        if (lastModified.HasValue)
        {
            response.Headers.LastModified = HeaderUtilities.FormatDate(lastModified.Value);
        }

        if (responseMessage.Headers.Vary.Count > 0)
        {
            response.Headers.Vary = string.Join(", ", responseMessage.Headers.Vary);
        }
    }

    private static string BuildDownloadFilename(BaseItem item, Uri remoteUri)
    {
        var remoteExtension = Path.GetExtension(remoteUri.LocalPath);
        var extension = string.IsNullOrEmpty(remoteExtension) ? Path.GetExtension(item.Path) : remoteExtension;
        var baseName = string.IsNullOrWhiteSpace(item.Name) ? "download" : item.Name;
        return string.Concat(baseName, extension).Replace("\"", string.Empty, StringComparison.Ordinal);
    }
}
