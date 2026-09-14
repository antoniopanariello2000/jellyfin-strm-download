using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDownload.Web;

/// <summary>
/// Intercepts HTTP responses for Jellyfin Web's index.html and injects the
/// script that redirects the native "Download" button to the .strm-aware
/// download endpoint. Requires no file system permissions.
/// </summary>
public class ScriptInjectionMiddleware
{
    private const string ScriptTag = "<script src=\"/Plugins/StrmDownload/ClientScript\" defer></script>";
    private const string Marker = "/Plugins/StrmDownload/ClientScript";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScriptInjectionMiddleware}"/> interface.</param>
    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.EnableNativeDownloadHook || !IsIndexHtmlRequest(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            var contentType = context.Response.ContentType ?? string.Empty;
            if (context.Response.StatusCode != StatusCodes.Status200OK
                || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                await CopyBackAsync(buffer, originalBody).ConfigureAwait(false);
                return;
            }

            buffer.Position = 0;
            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.OrdinalIgnoreCase) || bodyIndex == -1)
            {
                await CopyBackAsync(buffer, originalBody).ConfigureAwait(false);
                return;
            }

            var modified = html.Insert(bodyIndex, ScriptTag + "\n");
            var bytes = Encoding.UTF8.GetBytes(modified);

            context.Response.Headers.Remove("Content-Length");
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to inject the download script into index.html; passing through the original response.");
            await CopyBackAsync(buffer, originalBody).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task CopyBackAsync(MemoryStream buffer, Stream original)
    {
        if (buffer.Length > 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original).ConfigureAwait(false);
        }
    }

    private static bool IsIndexHtmlRequest(string path) =>
        path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/web", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
}
