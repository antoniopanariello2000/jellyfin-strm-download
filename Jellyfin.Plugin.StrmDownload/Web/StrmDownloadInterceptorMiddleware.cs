using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
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
/// intercepted: Jellyfin's own route is GET-only and answers HEAD with 405
/// Allow: GET, so a client that wants to size a download up front has nothing
/// to work with. No known client needs this today - it is offered because the
/// plugin can answer it correctly, not to work around the 405. Non-.strm items
/// and
/// missing items are left untouched and fall through to Jellyfin's own
/// controller, which handles them exactly as before. Authentication and the
/// user's download permission are checked here, because this middleware runs
/// before ASP.NET Core's authorization middleware.
/// </summary>
public class StrmDownloadInterceptorMiddleware
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> configured for upstream requests in
    /// <c>PluginServiceRegistrator</c>: it carries the plugin's User-Agent, asks
    /// for an unencoded body and caps redirects.
    /// </summary>
    public const string HttpClientName = "StrmDownload";

    /// <summary>
    /// Retry-After hint, in seconds, sent with the 503 that rejects a download
    /// over the configured concurrency limit. A short hint: slots free up as
    /// soon as any running download finishes.
    /// </summary>
    private const int RetryAfterSeconds = 30;

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

    /// <summary>
    /// Characters stripped from the offered filename. Path.GetInvalidFileNameChars
    /// only reflects the host's own rules - on Linux that is just NUL and '/' -
    /// but the file is saved by the client, commonly on Windows or an SMB share,
    /// so the stricter Windows set is applied on top.
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
        Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

    private static readonly Regex ExtensionRegex = new(
        @"^\.[A-Za-z0-9]{1,16}$",
        RegexOptions.Compiled);

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
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="concurrencyLimiter">The download concurrency limiter.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ILibraryManager libraryManager,
        IAuthorizationContext authContext,
        IHttpClientFactory httpClientFactory,
        IActivityManager activityManager,
        ILocalizationManager localization,
        DownloadConcurrencyLimiter concurrencyLimiter)
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

            // Rejecting rather than queueing: a queued request would sit on a
            // Jellyfin request thread and time out at the client anyway, while a
            // 503 with Retry-After tells the client what to do about it.
            using var slot = concurrencyLimiter.TryAcquire();
            if (slot is null)
            {
                _logger.LogWarning(
                    "Rejecting the .strm download of item {ItemId}: the configured limit of {MaxConcurrentDownloads} concurrent downloads is reached",
                    itemId,
                    Plugin.Instance.Configuration.MaxConcurrentDownloads);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return;
            }

            // Taking over the route also skips LibraryController.LogDownloadAsync,
            // so the entry is written here instead. Placed like the controller's:
            // after the permission check and before the content is served, so a
            // download that the client abandons half way still shows up.
            if (user is not null && ShouldLogDownload(context.Request))
            {
                await LogDownloadAsync(activityManager, localization, authInfo, item).ConfigureAwait(false);
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
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        var upstream = await SendUpstreamAsync(httpClient, context, remoteUri, isHeadRequest, cancellationToken).ConfigureAwait(false);
        using var upstreamResponse = upstream.Response;
        var probedLength = upstream.ProbedLength;

        var response = context.Response;
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            // Pass the upstream status through unchanged. 416 in particular has
            // to keep its "bytes */<total>" Content-Range, since that is what
            // lets a client correct an unsatisfiable range (RFC 9110 15.5.17) -
            // and it is how the Android client recognizes an already complete
            // download, by comparing its resume offset against the total.
            response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyAcceptRanges(upstreamResponse, response);

            var errorContentRange = upstreamResponse.Content.Headers.ContentRange;
            if (errorContentRange is not null)
            {
                response.Headers.ContentRange = FormatContentRange(errorContentRange);
            }
            else if (upstreamResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                _logger.LogWarning(
                    "Upstream {RemoteUri} answered 416 without a Content-Range header; clients cannot tell from this whether the download is already complete",
                    remoteUri);
            }

            return;
        }

        // A 206 that reaches the client without a usable Content-Range is worse
        // than an error: the official Jellyfin Android client reads the header
        // with requireNotNull and aborts the download before writing a byte.
        // Settle that before touching the response at all, so the 502 fallback
        // still has an untouched response to write.
        string? contentRange = null;
        if (!probedLength.HasValue && !TryResolveContentRange(upstreamResponse, context.Request, out contentRange))
        {
            _logger.LogError(
                "Upstream {RemoteUri} answered 206 without a usable Content-Range and the total length could not be derived; refusing to pass an incomplete partial response to the client",
                remoteUri);
            response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        // Both 200 and 206 pass through as they are. The probe path is the
        // exception: its 206 describes the one probe byte, not the answer this
        // HEAD is owed, so it is rewritten to a plain 200.
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

            CopyAcceptRanges(upstreamResponse, response);
            if (contentRange is not null)
            {
                response.Headers.ContentRange = contentRange;
            }
        }

        CopyValidatorHeaders(upstreamResponse, response);

        var filename = BuildDownloadFilename(item, remoteUri, upstreamResponse.Content.Headers.ContentType?.MediaType);
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            // filename is the ASCII fallback for clients that ignore RFC 5987,
            // filename* carries the real, UTF-8 encoded name (RFC 6266 4.1).
            FileName = ToAsciiFallback(filename),
            FileNameStar = filename
        }.ToString();

        if (isHeadRequest)
        {
            // A HEAD response carries the same headers as the GET would, but no
            // body (RFC 9110 9.3.2). Kestrel suppresses the body for HEAD and
            // skips its Content-Length verification for it, so the header set
            // above reaches the client as-is. Without this the request would
            // fall through to Jellyfin's GET-only route and be answered 405.
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
    /// Gets a value indicating whether this request should produce an activity
    /// log entry.
    /// </summary>
    /// <param name="request">The incoming client request.</param>
    /// <returns>Whether to log.</returns>
    private static bool ShouldLogDownload(HttpRequest request)
    {
        // HEAD is a metadata probe, not a download.
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        // One download is many ranged requests; logging every one would bury the
        // activity log. Count a download once, when it begins: no Range header
        // at all, or a Range whose first byte position is 0. A resume or a tail
        // probe ("bytes=-500", "bytes=900-") starts elsewhere and is skipped,
        // which also means a resumed download is not logged twice.
        if (!request.Headers.TryGetValue(HeaderNames.Range, out var rangeValues) || rangeValues.Count == 0)
        {
            return true;
        }

        var range = rangeValues.ToString();
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        return RangeHeaderValue.TryParse(range, out var parsedRange)
            && parsedRange.Ranges.Count > 0
            && parsedRange.Ranges.First().From == 0;
    }

    /// <summary>
    /// Writes the activity log entry Jellyfin's own controller would have
    /// written, so intercepted downloads still appear in Dashboard > Activity.
    /// </summary>
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="authInfo">The authorization info of the current request.</param>
    /// <param name="item">The item being downloaded.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task LogDownloadAsync(
        IActivityManager activityManager,
        ILocalizationManager localization,
        AuthorizationInfo authInfo,
        BaseItem item)
    {
        try
        {
            await activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    localization.GetServerLocalizedString("UserDownloadingItemWithValues"),
                    authInfo.User!.Username,
                    item.Name),
                "UserDownloadingContent",
                authInfo.UserId)
            {
                ShortOverview = string.Format(
                    CultureInfo.InvariantCulture,
                    localization.GetServerLocalizedString("AppDeviceValues"),
                    authInfo.Client,
                    authInfo.Device),
                ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture)
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never break the download itself.
            _logger.LogDebug(ex, "Could not write the activity log entry for item {ItemId}", item.Id);
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
    /// Copies Accept-Ranges from the upstream response as it was sent.
    /// </summary>
    /// <param name="responseMessage">The upstream response.</param>
    /// <param name="response">The client response.</param>
    private static void CopyAcceptRanges(HttpResponseMessage responseMessage, HttpResponse response)
    {
        if (responseMessage.Headers.AcceptRanges.Count > 0)
        {
            response.Headers.AcceptRanges = string.Join(", ", responseMessage.Headers.AcceptRanges);
        }
    }

    /// <summary>
    /// Determines the Content-Range to send to the client for a successful
    /// upstream response.
    /// <para>
    /// A 206 has to carry "bytes &lt;start&gt;-&lt;end&gt;/&lt;total&gt;" with a
    /// numeric total. RFC 9110 15.3.7 requires the header at all, and the
    /// official Jellyfin Android client parses it with requireNotNull and
    /// rejects a "*" total, so an incomplete one aborts the download before the
    /// first byte is written. When the upstream omits it, it can still be
    /// reconstructed for the one case where the total follows unambiguously:
    /// the client asked for the whole resource from byte 0, so the body is the
    /// entire resource and Content-Length is its total size.
    /// </para>
    /// </summary>
    /// <param name="responseMessage">The upstream response.</param>
    /// <param name="request">The incoming client request.</param>
    /// <param name="contentRange">
    /// The header value to send, or <c>null</c> when none is needed because the
    /// response is not a 206.
    /// </param>
    /// <returns>
    /// <c>false</c> when the response is a 206 whose Content-Range is missing or
    /// unusable and cannot be reconstructed. The caller must not forward such a
    /// response.
    /// </returns>
    private bool TryResolveContentRange(HttpResponseMessage responseMessage, HttpRequest request, out string? contentRange)
    {
        contentRange = null;
        var upstreamContentRange = responseMessage.Content.Headers.ContentRange;

        if (responseMessage.StatusCode != HttpStatusCode.PartialContent)
        {
            // 200 and friends: forward whatever the upstream sent, if anything.
            if (upstreamContentRange is not null)
            {
                contentRange = FormatContentRange(upstreamContentRange);
            }

            return true;
        }

        if (upstreamContentRange is not null && upstreamContentRange.HasRange && upstreamContentRange.HasLength)
        {
            WarnOnRangeStartMismatch(request, upstreamContentRange);
            contentRange = FormatContentRange(upstreamContentRange);
            return true;
        }

        var contentLength = responseMessage.Content.Headers.ContentLength;
        if (IsWholeResourceRange(request) && contentLength is > 0)
        {
            var total = contentLength.Value;
            contentRange = string.Format(CultureInfo.InvariantCulture, "bytes 0-{0}/{1}", total - 1, total);
            _logger.LogWarning(
                "Upstream answered 206 without a usable Content-Range; reconstructed \"{ContentRange}\" from Content-Length, since the client asked for the whole resource from byte 0",
                contentRange);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a value indicating whether the client asked for the entire resource
    /// starting at byte 0, i.e. an open ended "bytes=0-". Only then does the
    /// total length follow unambiguously from Content-Length.
    /// </summary>
    /// <param name="request">The incoming client request.</param>
    /// <returns>Whether the request covers the whole resource.</returns>
    private static bool IsWholeResourceRange(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Range, out var rangeValues) || rangeValues.Count == 0)
        {
            // No Range at all - a 206 is unexpected here, but the body is still
            // the whole resource.
            return true;
        }

        var range = rangeValues.ToString();
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        if (!RangeHeaderValue.TryParse(range, out var parsedRange) || parsedRange.Ranges.Count != 1)
        {
            return false;
        }

        var single = parsedRange.Ranges.First();
        return single.From == 0 && single.To is null;
    }

    /// <summary>
    /// Logs when the upstream's partial response starts somewhere other than
    /// where the client asked it to. A client resuming a download seeks to the
    /// offset it requested and would write the bytes to the wrong place.
    /// </summary>
    /// <param name="request">The incoming client request.</param>
    /// <param name="contentRange">The upstream's Content-Range.</param>
    private void WarnOnRangeStartMismatch(HttpRequest request, ContentRangeHeaderValue contentRange)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Range, out var rangeValues)
            || rangeValues.Count == 0
            || !RangeHeaderValue.TryParse(rangeValues.ToString(), out var parsedRange)
            || parsedRange.Ranges.Count != 1)
        {
            return;
        }

        var requestedFrom = parsedRange.Ranges.First().From;
        if (requestedFrom.HasValue && contentRange.From != requestedFrom)
        {
            _logger.LogWarning(
                "Upstream answered a partial response starting at byte {ActualFrom} although {RequestedFrom} was requested; a resuming client will write to the wrong offset",
                contentRange.From,
                requestedFrom);
        }
    }

    /// <summary>
    /// Formats a Content-Range header value explicitly, rather than relying on
    /// <see cref="ContentRangeHeaderValue.ToString"/>, so the wire format is
    /// pinned to "&lt;unit&gt; &lt;start&gt;-&lt;end&gt;/&lt;total&gt;" with
    /// invariant number formatting regardless of the server's culture.
    /// </summary>
    /// <param name="contentRange">The value to format.</param>
    /// <returns>The header value.</returns>
    private static string FormatContentRange(ContentRangeHeaderValue contentRange)
    {
        var unit = string.IsNullOrEmpty(contentRange.Unit) ? "bytes" : contentRange.Unit;
        var range = contentRange.HasRange
            ? string.Format(CultureInfo.InvariantCulture, "{0}-{1}", contentRange.From, contentRange.To)
            : "*";
        var length = contentRange.HasLength
            ? contentRange.Length!.Value.ToString(CultureInfo.InvariantCulture)
            : "*";

        return string.Format(CultureInfo.InvariantCulture, "{0} {1}/{2}", unit, range, length);
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

    /// <summary>
    /// Builds the filename offered to the client. Never derives the extension
    /// from the item's own path, which is the .strm file.
    /// </summary>
    /// <param name="item">The item being downloaded.</param>
    /// <param name="remoteUri">The URL read from the .strm file.</param>
    /// <param name="upstreamMediaType">The upstream Content-Type, without parameters.</param>
    /// <returns>A filename that is safe to write on the client.</returns>
    private string BuildDownloadFilename(BaseItem item, Uri remoteUri, string? upstreamMediaType)
        => SanitizeFileName(BuildBaseName(item)) + ResolveExtension(item, remoteUri, upstreamMediaType);

    /// <summary>
    /// Builds the base name, without extension, from the item's metadata.
    /// item.Name alone is only the episode title for an episode, which makes
    /// downloads of different series indistinguishable in a download folder.
    /// </summary>
    /// <param name="item">The item being downloaded.</param>
    /// <returns>The base name.</returns>
    private static string BuildBaseName(BaseItem item)
    {
        switch (item)
        {
            case Episode episode:
            {
                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(episode.SeriesName))
                {
                    parts.Add(episode.SeriesName);
                }

                // ParentIndexNumber is the season, IndexNumber the episode.
                if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                {
                    parts.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "S{0:00}E{1:00}",
                        episode.ParentIndexNumber.Value,
                        episode.IndexNumber.Value));
                }

                if (!string.IsNullOrWhiteSpace(episode.Name))
                {
                    parts.Add(episode.Name);
                }

                if (parts.Count > 0)
                {
                    return string.Join(" - ", parts);
                }

                break;
            }

            case Movie movie when !string.IsNullOrWhiteSpace(movie.Name):
            {
                return movie.ProductionYear.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", movie.Name, movie.ProductionYear.Value)
                    : movie.Name;
            }
        }

        return string.IsNullOrWhiteSpace(item.Name) ? "download" : item.Name;
    }

    /// <summary>
    /// Resolves the file extension, in descending order of trustworthiness: the
    /// remote URL's "extension" query parameter, the remote URL's own path, the
    /// container Jellyfin probed for the item's media source, the upstream
    /// Content-Type, and finally ".bin". The item's own path is never consulted
    /// - it is the .strm file, and handing the client a .strm is exactly the bug
    /// this plugin exists to fix.
    /// </summary>
    /// <param name="item">The item being downloaded.</param>
    /// <param name="remoteUri">The URL read from the .strm file.</param>
    /// <param name="upstreamMediaType">The upstream Content-Type, without parameters.</param>
    /// <returns>The extension, including the leading dot.</returns>
    private string ResolveExtension(BaseItem item, Uri remoteUri, string? upstreamMediaType)
    {
        // Checked before the path: NzbDAV2, the backend these .strm files point
        // at, carries the real extension in "?extension=mkv" while its path
        // carries an opaque id, so Path.GetExtension on the path alone comes back
        // empty and the download ends up named ".strm".
        var fromQuery = GetExtensionFromQuery(remoteUri);
        if (IsUsableExtension(fromQuery))
        {
            return fromQuery!;
        }

        var fromUrl = Path.GetExtension(remoteUri.LocalPath);
        if (IsUsableExtension(fromUrl))
        {
            return fromUrl;
        }

        foreach (var container in GetMediaSourceContainers(item))
        {
            // Container is sometimes a comma separated list of the formats
            // ffprobe matched, e.g. "mov,mp4,m4a,3gp,3g2,mj2"; the first entry
            // is the one to offer.
            var separatorIndex = container.IndexOf(',');
            var first = (separatorIndex >= 0 ? container[..separatorIndex] : container).Trim();
            if (first.Length == 0)
            {
                continue;
            }

            var fromContainer = first.StartsWith('.') ? first : "." + first;
            if (IsUsableExtension(fromContainer))
            {
                return fromContainer;
            }
        }

        if (!string.IsNullOrWhiteSpace(upstreamMediaType))
        {
            var fromContentType = MimeTypes.ToExtension(upstreamMediaType);
            if (IsUsableExtension(fromContentType))
            {
                return fromContentType!;
            }
        }

        return ".bin";
    }

    /// <summary>
    /// Reads an "extension" query parameter from the remote URL, if present.
    /// </summary>
    /// <param name="remoteUri">The URL read from the .strm file.</param>
    /// <returns>The extension including a leading dot, or <c>null</c>.</returns>
    private static string? GetExtensionFromQuery(Uri remoteUri)
    {
        if (string.IsNullOrEmpty(remoteUri.Query))
        {
            return null;
        }

        // ParseQuery matches keys case-insensitively and never returns null.
        if (!QueryHelpers.ParseQuery(remoteUri.Query).TryGetValue("extension", out var values))
        {
            return null;
        }

        var extension = values.ToString().Trim();
        if (extension.Length == 0)
        {
            return null;
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }

    /// <summary>
    /// Reads the containers of the item's media sources, tolerating a failure:
    /// an unknown extension is a cosmetic problem and must not fail a download
    /// that is otherwise fine.
    /// </summary>
    /// <param name="item">The item being downloaded.</param>
    /// <returns>The containers, possibly empty.</returns>
    private IEnumerable<string> GetMediaSourceContainers(BaseItem item)
    {
        IReadOnlyList<MediaSourceInfo> mediaSources;
        try
        {
            mediaSources = item.GetMediaSources(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the media sources of item {ItemId}", item.Id);
            return [];
        }

        return mediaSources
            .Select(mediaSource => mediaSource.Container)
            .Where(container => !string.IsNullOrWhiteSpace(container))!;
    }

    /// <summary>
    /// Gets a value indicating whether an extension can be offered to the
    /// client. ".strm" never can. Candidates come from a remote URL and from
    /// upstream headers, so the shape is constrained rather than trusted: a dot
    /// followed by up to 16 alphanumerics, which covers every real container
    /// and leaves no room for separators or traversal.
    /// </summary>
    /// <param name="extension">The candidate extension.</param>
    /// <returns>Whether the extension is usable.</returns>
    private static bool IsUsableExtension(string? extension)
        => extension is not null
            && ExtensionRegex.IsMatch(extension)
            && !string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes characters that are not legal in a filename on the platforms a
    /// client is likely to save to.
    /// </summary>
    /// <param name="value">The raw name.</param>
    /// <returns>The sanitized name, never empty.</returns>
    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character) || Array.IndexOf(InvalidFileNameChars, character) >= 0)
            {
                continue;
            }

            builder.Append(character);
        }

        // Trailing dots and spaces are silently dropped by Windows, which would
        // detach the extension we are about to append.
        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "download" : sanitized;
    }

    /// <summary>
    /// Produces the ASCII form used for the plain <c>filename</c> parameter.
    /// Letters carrying diacritics are folded to their base letter rather than
    /// dropped, so the name stays readable for clients that ignore
    /// <c>filename*</c>. Anything else outside ASCII becomes an underscore.
    /// </summary>
    /// <param name="value">The sanitized UTF-8 name.</param>
    /// <returns>An ASCII-only name.</returns>
    private static string ToAsciiFallback(string value)
    {
        // Canonical decomposition splits e.g. "u" + combining diaeresis apart,
        // so the combining mark can be dropped on its own.
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character <= 0x7F ? character : '_');
        }

        var ascii = builder.ToString().Trim();
        return ascii.Length == 0 ? "download" : ascii;
    }
}
