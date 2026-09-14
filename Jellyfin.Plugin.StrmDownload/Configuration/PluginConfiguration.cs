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
}
