using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Web;

/// <summary>
/// Registers the station page's web transformations with the File Transformation plugin at startup, when it is
/// installed. The plugin is optional: without it Jellywave works as before and the configuration page says why
/// the station page is unavailable. The call goes through reflection because plugins cannot reference each other,
/// and the payload is built with the other plugin's own JSON types so nothing crosses assembly load contexts.
/// </summary>
public sealed class WebUiStartupTask : IScheduledTask
{
    /// <summary>Stable ids so a re-registration replaces rather than duplicates.</summary>
    private static readonly Guid IndexHtmlId = Guid.Parse("c0b9d1b2-3b2b-4e1a-9d7a-6ca437a05b11");
    private static readonly Guid ConfigJsonId = Guid.Parse("c0b9d1b2-3b2b-4e1a-9d7a-6ca437a05b12");

    private readonly ILogger<WebUiStartupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebUiStartupTask"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public WebUiStartupTask(ILogger<WebUiStartupTask> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the last registration attempt.
    /// </summary>
    public static WebUiStatus Status { get; private set; } = new() { State = WebUiState.Unknown, Message = "Not checked yet." };

    /// <inheritdoc />
    public string Name => "Jellywave station page";

    /// <inheritdoc />
    public string Key => "Jellywave.WebUi";

    /// <inheritdoc />
    public string Description => "Registers the Jellywave station page with the File Transformation plugin (runs at startup).";

    /// <inheritdoc />
    public string Category => "Jellywave";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Status = Register();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    private WebUiStatus Register()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(a => a.GetName().Name?.EndsWith(".FileTransformation", StringComparison.Ordinal) == true);
        if (assembly is null)
        {
            _logger.LogInformation("File Transformation plugin not installed; the station page is unavailable (the tile keeps working)");
            return new WebUiStatus { State = WebUiState.NotInstalled, Message = "The File Transformation plugin is not installed." };
        }

        var version = assembly.GetName().Version?.ToString();
        try
        {
            var pluginInterface = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?? throw new InvalidOperationException("PluginInterface type not found");
            var register = pluginInterface.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("RegisterTransformation method not found");
            var payloadType = register.GetParameters().Single().ParameterType;   // Newtonsoft JObject, from that plugin's load context
            var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
                ?? throw new InvalidOperationException(payloadType.Name + " has no Parse(string)");

            var callbackAssembly = typeof(WebUiTransforms).Assembly.FullName;
            var callbackClass = typeof(WebUiTransforms).FullName;
            // Plain file names, not anchored regexes: File Transformation keeps one pipeline per pattern string and
            // runs only the first pipeline whose pattern matches a request, so a plugin that registers a different
            // pattern for the same file never runs once another plugin registered the plain name. The plain name is
            // the convention (SeerrFin and others use it), and joining it chains the callbacks.
            foreach (var (id, pattern, method) in new[]
            {
                (IndexHtmlId, "index.html", nameof(WebUiTransforms.IndexHtml)),
                (ConfigJsonId, "config.json", nameof(WebUiTransforms.ConfigJson))
            })
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["id"] = id.ToString(),
                    ["fileNamePattern"] = pattern,
                    ["callbackAssembly"] = callbackAssembly,
                    ["callbackClass"] = callbackClass,
                    ["callbackMethod"] = method
                });
                register.Invoke(null, [parse.Invoke(null, [json])]);
            }

            _logger.LogInformation("Station page registered with File Transformation {Version}", version);
            return new WebUiStatus { State = WebUiState.Active, Message = "Registered; the page is served with the web client.", FileTransformationVersion = version };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering the station page with File Transformation {Version} failed", version);
            return new WebUiStatus { State = WebUiState.Failed, Message = "Registration failed; see the server log.", FileTransformationVersion = version };
        }
    }
}
