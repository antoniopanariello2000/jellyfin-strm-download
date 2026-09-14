using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StrmDownload.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin should inject the
    /// client-side script that redirects the native "Download" button in
    /// Jellyfin Web to the .strm-aware download endpoint. Disable this if it
    /// ever conflicts with a Jellyfin Web update, without uninstalling the plugin.
    /// </summary>
    public bool EnableNativeDownloadHook { get; set; } = true;
}
