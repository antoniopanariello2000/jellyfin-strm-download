using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDownload.Api;

/// <summary>
/// A drop-in replacement for the native Items/{itemId}/Download endpoint that
/// also knows how to resolve .strm files to the remote content they point to.
/// </summary>
[ApiController]
[Route("Plugins/StrmDownload")]
public class StrmDownloadController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StrmDownloadController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmDownloadController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{StrmDownloadController}"/> interface.</param>
    public StrmDownloadController(
        ILibraryManager libraryManager,
        IAuthorizationContext authContext,
        IHttpClientFactory httpClientFactory,
        ILogger<StrmDownloadController> logger)
    {
        _libraryManager = libraryManager;
        _authContext = authContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Downloads an item, resolving .strm files to their remote target instead
    /// of serving the .strm text file itself.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The downloaded file.</returns>
    [HttpGet("Items/{itemId}/Download")]
    [Authorize(Policy = Policies.Download)]
    public async Task<ActionResult> GetStrmAwareDownload([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var authInfo = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        var user = authInfo.User;

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var canDownload = user is not null ? item.CanDownload(user) : item.CanDownload();
        if (!canDownload)
        {
            return Forbid();
        }

        if (!string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            // Not a .strm item: behave exactly like the native download endpoint.
            var filename = Path.GetFileName(item.Path)?.Replace("\"", string.Empty, StringComparison.Ordinal);
            return PhysicalFile(item.Path, MimeTypes.GetMimeType(item.Path), filename, true);
        }

        return await ProxyStrmDownloadAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult> ProxyStrmDownloadAsync(BaseItem item, CancellationToken cancellationToken)
    {
        string strmContent;
        try
        {
            strmContent = await System.IO.File.ReadAllTextAsync(item.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to read .strm file {Path}", item.Path);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to read the .strm file.");
        }

        var remoteUrl = strmContent.Trim();
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri))
        {
            _logger.LogError("The .strm file {Path} does not contain a valid absolute URL", item.Path);
            return StatusCode(StatusCodes.Status502BadGateway, "The .strm file does not contain a valid URL.");
        }

        var httpClient = _httpClientFactory.CreateClient();
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            requestMessage.Headers.TryAddWithoutValidation("Range", (string?)range);
        }

        HttpResponseMessage responseMessage;
        try
        {
            responseMessage = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach the remote URL {Url} referenced by {Path}", remoteUri, item.Path);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to reach the remote file referenced by the .strm file.");
        }

        using (responseMessage)
        {
            if (!responseMessage.IsSuccessStatusCode)
            {
                return StatusCode((int)responseMessage.StatusCode, "The remote server returned an error for this download.");
            }

            Response.StatusCode = (int)responseMessage.StatusCode;
            Response.ContentType = responseMessage.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (responseMessage.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = responseMessage.Content.Headers.ContentLength;
            }

            if (responseMessage.Headers.AcceptRanges.Count > 0)
            {
                Response.Headers.AcceptRanges = "bytes";
            }

            var filename = BuildDownloadFilename(item, remoteUri);
            Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = filename
            }.ToString();

            await using var remoteStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await remoteStream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);

            return new EmptyResult();
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
