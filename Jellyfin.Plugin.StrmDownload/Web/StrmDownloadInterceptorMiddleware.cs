using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDownload.Web;

/// <summary>
/// Intercepts the native "Items/{itemId}/Download" request, before it reaches
/// Jellyfin's own controller, and serves the remote content a .strm file
/// points to instead of the .strm text file itself. This fixes downloads for
/// every client (Jellyfin Web, mobile apps, etc.) since they all call the
/// same native URL - no client-side changes needed. Non-.strm items, missing
/// items and failed authorization are left untouched and fall through to
/// Jellyfin's own controller, which handles them exactly as before.
/// </summary>
public class StrmDownloadInterceptorMiddleware
{
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
            || !HttpMethods.IsGet(context.Request.Method))
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
            var authInfo = await authContext.GetAuthorizationInfo(context).ConfigureAwait(false);
            var user = authInfo.User;

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

        var httpClient = httpClientFactory.CreateClient();
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        if (context.Request.Headers.TryGetValue("Range", out var range))
        {
            requestMessage.Headers.TryAddWithoutValidation("Range", (string?)range);
        }

        using var responseMessage = await httpClient
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!responseMessage.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)responseMessage.StatusCode;
            return;
        }

        var response = context.Response;
        response.StatusCode = (int)responseMessage.StatusCode;
        response.ContentType = responseMessage.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (responseMessage.Content.Headers.ContentLength.HasValue)
        {
            response.ContentLength = responseMessage.Content.Headers.ContentLength;
        }

        if (responseMessage.Headers.AcceptRanges.Count > 0)
        {
            response.Headers.AcceptRanges = "bytes";
        }

        var filename = BuildDownloadFilename(item, remoteUri);
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = filename
        }.ToString();

        await using var remoteStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await remoteStream.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildDownloadFilename(BaseItem item, Uri remoteUri)
    {
        var remoteExtension = Path.GetExtension(remoteUri.LocalPath);
        var extension = string.IsNullOrEmpty(remoteExtension) ? Path.GetExtension(item.Path) : remoteExtension;
        var baseName = string.IsNullOrWhiteSpace(item.Name) ? "download" : item.Name;
        return string.Concat(baseName, extension).Replace("\"", string.Empty, StringComparison.Ordinal);
    }
}
