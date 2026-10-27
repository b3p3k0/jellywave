using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Jellywave.Web;

/// <summary>
/// Callbacks the File Transformation plugin invokes (by assembly, class and method name) while serving
/// jellyfin-web files. Both are pure string edits; a file that does not look as expected is returned unchanged.
/// </summary>
public static partial class WebUiTransforms
{
    /// <summary>
    /// Name under which the web client finds the plugin class (<c>window.JellywaveUi</c>); also the entry
    /// added to <c>config.json</c>'s <c>plugins</c> list so jellyfin-web's plugin manager instantiates it
    /// with its playback manager and router.
    /// </summary>
    public const string WebPluginName = "JellywaveUi";

    /// <summary>
    /// Adds the stylesheet and the script to <c>index.html</c>. The script tag is synchronous and sits at the end
    /// of <c>head</c> so <c>window.JellywaveUi</c> exists before the deferred bundles run the plugin loader.
    /// Relative URLs keep base-URL and reverse-proxy setups working (<c>/web/index.html</c> → <c>/Jellywave/...</c>).
    /// </summary>
    /// <param name="payload">The file as served.</param>
    /// <returns>The transformed file.</returns>
    public static string IndexHtml(TransformPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var html = payload.Contents ?? string.Empty;
        if (html.Contains("Jellywave/ui.js", StringComparison.Ordinal))
        {
            return html;
        }

        var version = Uri.EscapeDataString(AssetVersion());
        var tags = string.Create(
            CultureInfo.InvariantCulture,
            $"<link rel=\"stylesheet\" href=\"../Jellywave/ui.css?v={version}\" /><script src=\"../Jellywave/ui.js?v={version}\"></script>");
        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html : html.Insert(at, tags);
    }

    /// <summary>
    /// Adds <see cref="WebPluginName"/> to the <c>plugins</c> array of <c>config.json</c>.
    /// </summary>
    /// <param name="payload">The file as served.</param>
    /// <returns>The transformed file.</returns>
    public static string ConfigJson(TransformPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = payload.Contents ?? string.Empty;
        if (json.Contains("\"" + WebPluginName + "\"", StringComparison.Ordinal))
        {
            return json;
        }

        return PluginsArrayStart().Replace(json, m => m.Value + "\"" + WebPluginName + "\"" + (m.Groups["close"].Success ? string.Empty : ", "), 1);
    }

    /// <summary>
    /// Cache-busting token for the injected assets: the plugin's assembly version.
    /// </summary>
    /// <returns>The version string.</returns>
    public static string AssetVersion()
        => typeof(WebUiTransforms).Assembly.GetName().Version?.ToString() ?? "0";

    [GeneratedRegex("\"plugins\"\\s*:\\s*\\[\\s*(?=(?<close>\\])|)")]
    private static partial Regex PluginsArrayStart();
}
