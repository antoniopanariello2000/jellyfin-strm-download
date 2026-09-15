using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StrmDownload.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin should intercept
    /// the native "Items/{itemId}/Download" request and resolve .strm files
    /// to their remote target. Disable this to fall back to Jellyfin's
    /// default behavior for all downloads, without uninstalling the plugin.
    /// </summary>
    public bool EnableNativeDownloadHook { get; set; } = true;

    /// <summary>
    /// Gets or sets the idle timeout, in seconds, applied while streaming the
    /// remote content to the client. The timeout is reset after every chunk
    /// that is successfully read from the upstream server, so it aborts only
    /// stalled transfers and never a long but healthy download. Set to 0 to
    /// disable the timeout.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the maximum number of .strm downloads proxied at the same
    /// time. Further requests are rejected with 503 and a Retry-After hint
    /// rather than queued. Set to 0 for no limit.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; }
}
