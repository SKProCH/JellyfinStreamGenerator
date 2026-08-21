using Jellyfin.Plugin.StreamGenerator.Decorators;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using Jellyfin.Plugin.StreamGenerator.Progress;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamGenerator;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStreamGeneratorConfigurationAccessor, StreamGeneratorConfigurationAccessor>();
        serviceCollection.Decorate<IAuthorizationContext, CustomStreamTokensAuthorizationContext>();
        serviceCollection.AddSingleton<IAdvancedTranscodeManager, AdvancedTranscodeManager>();
        serviceCollection.AddSingleton<IPlaybackProgressTracker, PlaybackProgressTracker>();
        serviceCollection.Configure<MvcOptions>(opts => opts.Filters.Add<DynamicHlsContentInterceptionFilter>());  
        serviceCollection.AddSingleton<DynamicHlsContentInterceptionFilter>(); 
    }
}
