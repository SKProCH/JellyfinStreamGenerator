using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
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
    private const string WebFilePattern = @".*(?:main\.jellyfin\.bundle|\.chunk)\.js$";

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
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);

            if (ftAssembly != null)
            {
                if (TryRegisterFileTransformationDirectly(ftAssembly))
                {
                    _logger.LogInformation("Successfully registered FileTransformation for Jellyfin web bundles");
                }
                else if (TryRegisterFileTransformationViaPluginInterface(ftAssembly))
                {
                    _logger.LogInformation("Successfully registered FileTransformation through PluginInterface");
                }
                else
                {
                    _logger.LogWarning("FileTransformation plugin interface was found, but Stream Generator could not register a transformation");
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

    private bool TryRegisterFileTransformationDirectly(Assembly ftAssembly)
    {
        try
        {
            var pluginType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.FileTransformationPlugin");
            var writeServiceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService");
            var transformDelegateType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.Library.TransformFile");
            var transformMethod = typeof(StreamGeneratorPlugin).GetMethod(
                nameof(TransformContextMenuFile),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (pluginType == null || writeServiceType == null || transformDelegateType == null || transformMethod == null)
            {
                return false;
            }

            var pluginInstance = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var serviceProvider = pluginType.GetProperty("ServiceProvider")?.GetValue(pluginInstance) as IServiceProvider;
            var writeService = serviceProvider?.GetService(writeServiceType);
            var addTransformationMethod = writeServiceType.GetMethod("AddTransformation");

            if (writeService == null || addTransformationMethod == null)
            {
                return false;
            }

            var transformDelegate = Delegate.CreateDelegate(transformDelegateType, transformMethod);
            addTransformationMethod.Invoke(writeService, new object[] { Id, WebFilePattern, transformDelegate });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct FileTransformation registration failed; falling back to PluginInterface");
            return false;
        }
    }

    private bool TryRegisterFileTransformationViaPluginInterface(Assembly ftAssembly)
    {
        try
        {
            var pluginType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            var payloadType = registerMethod?.GetParameters().SingleOrDefault()?.ParameterType;
            var parseMethod = payloadType?.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (registerMethod == null || parseMethod == null)
            {
                return false;
            }

            var payloadJson = JsonConvert.SerializeObject(new
            {
                id = Id.ToString(),
                fileNamePattern = WebFilePattern,
                callbackAssembly = GetType().Assembly.FullName,
                callbackClass = typeof(StreamGeneratorPlugin).FullName,
                callbackMethod = nameof(PatchContextMenu)
            });

            var payload = parseMethod.Invoke(null, new object[] { payloadJson });
            registerMethod.Invoke(null, new[] { payload });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PluginInterface FileTransformation registration failed");
            return false;
        }
    }

    private static async Task TransformContextMenuFile(string path, Stream contents)
    {
        using var reader = new StreamReader(contents, leaveOpen: true);
        var currentContent = await reader.ReadToEndAsync().ConfigureAwait(false);
        var transformedContent = PatchContextMenu(new PatchRequestPayload { Contents = currentContent });

        contents.Seek(0, SeekOrigin.Begin);
        contents.SetLength(0);

        await using var writer = new StreamWriter(contents, leaveOpen: true);
        await writer.WriteAsync(transformedContent).ConfigureAwait(false);
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
            var generateStreamObj = @"{name:""Generate Stream URL"",id:""generate-stream"",icon:""link""}";

            // Keep the replacement minified so it does not break the web bundle's "use strict" structure.
            // Copy Stream URL is only a placement landmark; our command is pushed separately after that block so hiding Jellyfin's default copy command with CSS will not hide Generate Stream URL.
            var regexContext = Regex.Replace(
                payload.Contents,
                @"(id:""copy-stream"",icon:""content_copy""\}\)\)\),)",
                $"${{1}}c&&\"Photo\"!==i.MediaType&&d.push({generateStreamObj}),"
            );

            if (regexContext == payload.Contents)
            {
                regexContext = Regex.Replace(
                    payload.Contents,
                    @"(id:""copy-stream"",icon:""content_copy""\})",
                    $"${{1}},{generateStreamObj}"
                );
            }

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
