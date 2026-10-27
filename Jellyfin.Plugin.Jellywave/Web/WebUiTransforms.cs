using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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

    /// <summary>Manifest resource name of the injected script.</summary>
    internal const string ScriptResource = "Jellyfin.Plugin.Jellywave.Web.ui.js";

    /// <summary>Manifest resource name of the injected stylesheet.</summary>
    internal const string StyleResource = "Jellyfin.Plugin.Jellywave.Web.ui.css";

    /// <summary>Hex characters of the content hash kept in the asset version (48 bits).</summary>
    private const int AssetVersionLength = 12;

    private static readonly Lazy<string> EmbeddedAssetVersion = new(() => ContentVersion(ReadAsset(ScriptResource), ReadAsset(StyleResource)));

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
    /// Cache-busting token for the injected assets (the <c>?v=</c> query and the ETag): a hash of the embedded
    /// script and stylesheet, so any change to either reaches browsers even when the plugin version stays the same.
    /// Computed once per process.
    /// </summary>
    /// <returns>The version string.</returns>
    public static string AssetVersion() => EmbeddedAssetVersion.Value;

    /// <summary>
    /// The first <see cref="AssetVersionLength"/> hex characters of the SHA-256 over the parts, each prefixed with
    /// its length so moving bytes from one part to the next changes the result. Internal for tests.
    /// </summary>
    /// <param name="parts">The asset contents, in a fixed order.</param>
    /// <returns>The version string.</returns>
    internal static string ContentVersion(params IReadOnlyList<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(BitConverter.GetBytes((long)part.Length));
            hash.AppendData(part);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..AssetVersionLength];
    }

    private static byte[] ReadAsset(string resource)
    {
        using var stream = typeof(WebUiTransforms).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [GeneratedRegex("\"plugins\"\\s*:\\s*\\[\\s*(?=(?<close>\\])|)")]
    private static partial Regex PluginsArrayStart();
}
