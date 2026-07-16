namespace Jellyfin.Plugin.StreamGenerator.Configuration;

public sealed class StreamGeneratorConfigurationAccessor : IStreamGeneratorConfigurationAccessor
{
    public PluginConfiguration? Configuration => StreamGeneratorPlugin.Instance?.Configuration;

    public void Save() => StreamGeneratorPlugin.Instance?.SaveConfiguration();
}
