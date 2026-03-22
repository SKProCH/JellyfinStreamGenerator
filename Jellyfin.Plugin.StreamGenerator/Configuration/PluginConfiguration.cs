using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamGenerator.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
    }

    public bool GenerateCustomApiTokens { get; set; } = true;
    public double? DefaultCustomTokenDurationHours { get; set; } = null;
    public double? MaxCustomTokenDurationHours { get; set; } = null;

    public Dictionary<string, StreamTokenInformation> StreamTokens { get; set; } = new();
}
