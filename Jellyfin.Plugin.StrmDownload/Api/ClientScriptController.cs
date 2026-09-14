using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StrmDownload.Api;

/// <summary>
/// Serves the client-side script injected into Jellyfin Web.
/// </summary>
[ApiController]
[Route("Plugins/StrmDownload")]
[AllowAnonymous]
public class ClientScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.StrmDownload.Web.strm-download-client.js";

    /// <summary>
    /// Gets the client-side script that redirects the native Download button
    /// to the .strm-aware download endpoint.
    /// </summary>
    /// <returns>The script content.</returns>
    [HttpGet("ClientScript")]
    [Produces("application/javascript")]
    public async Task<ActionResult> GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync().ConfigureAwait(false);
        return Content(contents, "application/javascript");
    }
}
