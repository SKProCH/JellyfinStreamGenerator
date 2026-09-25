using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using Jellyfin.Plugin.StreamGenerator.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamGenerator;

/// <summary>
/// The main plugin.
/// </summary>
public class StreamGeneratorPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<StreamGeneratorPlugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamGeneratorPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public StreamGeneratorPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<StreamGeneratorPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;

        // Load config from JSON, overriding the base XML lazy-load
        Configuration = LoadJsonConfiguration();

        RegisterFileTransformation();
    }

    /// <inheritdoc />
    public override string Name => "Stream Generator";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("E5A2A3B4-11D5-4F8A-9E2A-6D4B7A9B3C1D");

    /// <inheritdoc />
    public override string ConfigurationFileName => Path.ChangeExtension(AssemblyFileName, ".json");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static StreamGeneratorPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Web.configurationPage.html",
            }
        };
    }

    /// <inheritdoc />
    public override void SaveConfiguration(PluginConfiguration config)
    {
        var folder = Path.GetDirectoryName(ConfigurationFilePath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(ConfigurationFilePath, JsonConvert.SerializeObject(config, Formatting.Indented));
    }

    private PluginConfiguration LoadJsonConfiguration()
    {
        try
        {
            if (File.Exists(ConfigurationFilePath))
            {
                var json = File.ReadAllText(ConfigurationFilePath);
                return JsonConvert.DeserializeObject<PluginConfiguration>(json) ?? new PluginConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin configuration from JSON");
        }

        return new PluginConfiguration();
    }

    private void RegisterFileTransformation()
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains("FileTransformation") ?? false);

            if (ftAssembly != null)
            {
                var pluginType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                if (pluginType != null)
                {
                    var payload = new JObject();
                    payload.Add("id", Id.ToString());
                    payload.Add("fileNamePattern", ".*\\.chunk\\.js$");
                    payload.Add("callbackAssembly", GetType().Assembly.FullName);
                    payload.Add("callbackClass", typeof(StreamGeneratorPlugin).FullName);
                    payload.Add("callbackMethod", nameof(PatchContextMenu));

                    pluginType.GetMethod("RegisterTransformation")?.Invoke(null, new object[] { payload });
                    _logger.LogInformation("Successfully registered FileTransformation for chunk.js via JObject.Add");
                }
            }
            else
            {
                _logger.LogWarning("FileTransformation plugin not found! Stream Generator will not work without it");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register file transformation");
        }
    }

    /// <summary>
    /// This method is called by the FileTransformation plugin when itemContextMenu.js is requested.
    /// </summary>
    /// <param name="payload">The original JS file content.</param>
    /// <returns>The patched JS file content.</returns>
    public static string PatchContextMenu(PatchRequestPayload payload)
    {
        Debug.Assert(payload.Contents != null, "Payload contents are null");

        try
        {
            var generateStreamObj = @",{name:""Generate Stream URL"",id:""generate-stream"",icon:""link""}";

            // Make sure the replacement strings don't contain any newlines or indentation that breaks "use strict"; minified structure
            // We append our object right after the "copy-stream" object, effectively passing it as a second argument to .push() or next element in an array.
            var regexContext = Regex.Replace(
                payload.Contents,
                @"(id:""copy-stream"",icon:""content_copy""\})",
                $"$1{generateStreamObj}"
            );

            // Do not rely on minifier-generated local names. Jellyfin web changes those names
            // between releases, while the command handler itself keeps the same three arguments.
            var copyStreamCaseIndex = regexContext.IndexOf("case\"copy-stream\"", StringComparison.Ordinal);
            if (copyStreamCaseIndex < 0)
            {
                return payload.Contents;
            }

            var functionMatches = Regex.Matches(
                regexContext[..copyStreamCaseIndex],
                @"function(?:\s+[A-Za-z_$][\w$]*)?\s*\(\s*([A-Za-z_$][\w$]*)\s*,\s*([A-Za-z_$][\w$]*)\s*,\s*([A-Za-z_$][\w$]*)\s*\)\s*\{");

            if (functionMatches.Count == 0)
            {
                Instance?._logger.LogWarning("Could not find the item command handler while patching itemContextMenu.js");
                return payload.Contents;
            }

            var commandFunction = functionMatches[^1];
            var itemArgument = commandFunction.Groups[1].Value;
            var commandFunctionStart = commandFunction.Index + commandFunction.Length;
            var contextSetup =
                $"window.streamGeneratorCommandContext={{item:{itemArgument},serverId:{itemArgument}.ServerId}};";
            regexContext = regexContext.Insert(commandFunctionStart, contextSetup);

            var generateStreamCase =
                @"case""generate-stream"":(function(){var context=window.streamGeneratorCommandContext;if(!context){console.error(""StreamGenerator: command context not found"");return}var open=function(){if(window.showStreamGeneratorPopup){window.showStreamGeneratorPopup(context.item.Id,context.serverId)}else{console.error(""StreamGenerator: popup script did not register showStreamGeneratorPopup"")}};if(window.streamGeneratorPopupPromise){window.streamGeneratorPopupPromise.then(open).catch(function(e){console.error(""StreamGenerator popup script failed to load"",e)})}else{open()}})();break;";

            var regexCase = Regex.Replace(
                regexContext,
                @"(case""copy-stream"")",
                $"{generateStreamCase}$1"
            );

            if (regexCase == payload.Contents)
            {
                return payload.Contents;
            }

            // Load the popup as a separate resource so updates do not depend on the cached Jellyfin chunk.
            var popupLoader =
                "window.streamGeneratorPopupPromise=import(window.ApiClient.getUrl('StreamGenerator/PopupContent.js')).catch(function(error){console.error('StreamGenerator: Failed to load popup script',error);return null;});";
            var finalResult = regexCase + "\n" + popupLoader;

            return finalResult;
        }
        catch (Exception ex)
        {
            Instance?._logger.LogError(ex, "Error patching itemContextMenu.js");
            return payload.Contents ?? $"Error patching itemContextMenu.js, {ex}";
        }
    }

}
