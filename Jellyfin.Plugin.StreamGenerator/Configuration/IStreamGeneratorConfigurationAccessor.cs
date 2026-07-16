namespace Jellyfin.Plugin.StreamGenerator.Configuration;

public interface IStreamGeneratorConfigurationAccessor
{
    PluginConfiguration? Configuration { get; }

    void Save();
}
