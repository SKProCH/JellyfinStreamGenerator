using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
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
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;

        RegisterFileTransformation();
    }

    /// <inheritdoc />
    public override string Name => "Stream Generator";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("E5A2A3B4-11D5-4F8A-9E2A-6D4B7A9B3C1D");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return Array.Empty<PluginPageInfo>();
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
                    payload.Add("callbackClass", typeof(Plugin).FullName);
                    payload.Add("callbackMethod", nameof(PatchContextMenu));

                    pluginType.GetMethod("RegisterTransformation")?.Invoke(null, new object[] { payload });
                    _logger.LogInformation("Successfully registered FileTransformation for chunk.js via JObject.Add");
                }
            }
            else
            {
                _logger.LogWarning("FileTransformation plugin not found! Stream Generator will not work without it.");
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

            var generateStreamCase = @"case""generate-stream"":if(window.showStreamGeneratorPopup){window.showStreamGeneratorPopup(c,u)}else{console.error(""StreamGenerator popup script not loaded!"")}try{k(l,t)()}catch(e){console.error(""StreamGenerator: Error calling getResolveFunction"",e)}break;";

            var regexCase = Regex.Replace(
                regexContext,
                @"(case""copy-stream"")",
                $"{generateStreamCase}$1"
            );

            if (regexCase == payload.Contents)
            {
                return payload.Contents;
            }

            var popupJs = GetPopupScriptFromResources();
            var finalResult = regexCase + "\n" + popupJs;

            return finalResult;
        }
        catch (Exception ex)
        {
            Instance?._logger.LogError(ex, "Error patching itemContextMenu.js");
            return payload.Contents ?? $"Error patching itemContextMenu.js, {ex}";
        }
    }

    private static string GetPopupScriptFromResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Jellyfin.Plugin.StreamGenerator.Web.PopupContent.js";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return "console.error('StreamGenerator: PopupContent.js resource not found');";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
