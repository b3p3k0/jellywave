using System;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Builds the station URLs from configuration. Pure functions, no I/O.
/// </summary>
public static class StationUrls
{
    /// <summary>Refusal: the value does not parse as an absolute URL.</summary>
    public const string RefusedUnparsable = "it is not an absolute URL";

    /// <summary>Refusal: the scheme is not http or https.</summary>
    public const string RefusedScheme = "only http:// and https:// are allowed";

    /// <summary>Refusal: the value carries userinfo (the userinfo itself is never repeated).</summary>
    public const string RefusedCredentials = "it contains credentials (user:password@)";

    /// <summary>Refusal: the value carries a query.</summary>
    public const string RefusedQuery = "it contains a query (?…)";

    /// <summary>Refusal: the value carries a fragment.</summary>
    public const string RefusedFragment = "it contains a fragment (#…)";

    /// <summary>
    /// Tries to build the station base URL with a trailing slash. A path prefix is kept
    /// (for example <c>http://host/radio</c> becomes <c>http://host/radio/</c>).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="baseUrl">The base URL, or null when unset, not an absolute http(s) URL, or carrying credentials, a query or a fragment.</param>
    /// <returns>True when the base URL is usable.</returns>
    public static bool TryGetBaseUrl(PluginConfiguration config, out Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TryParseBaseUrl(config.StationBaseUrl, out baseUrl);
    }

    /// <summary>
    /// Parses a station base URL string the way <see cref="TryGetBaseUrl"/> does.
    /// </summary>
    /// <param name="rawBaseUrl">The raw value, possibly null or padded.</param>
    /// <param name="baseUrl">The base URL with a trailing slash, or null.</param>
    /// <returns>
    /// True when the value is an absolute http(s) URL with no userinfo (not even an empty <c>@</c>), no query and no
    /// fragment. HTTP is allowed: the station normally runs on plain HTTP on the LAN.
    /// </returns>
    public static bool TryParseBaseUrl(string? rawBaseUrl, out Uri? baseUrl)
        => CheckBaseUrl(rawBaseUrl, out baseUrl) is null && baseUrl is not null;

    /// <summary>
    /// Says why a non-empty station base URL is refused, so a saved value that no longer passes is not mistaken
    /// for "not configured". The reason is a fixed sentence and never repeats any part of the value.
    /// </summary>
    /// <param name="rawBaseUrl">The raw value, possibly null or padded.</param>
    /// <returns>
    /// Null when the value is blank (not configured) or usable; otherwise one of <see cref="RefusedUnparsable"/>,
    /// <see cref="RefusedScheme"/>, <see cref="RefusedCredentials"/>, <see cref="RefusedQuery"/> or <see cref="RefusedFragment"/>.
    /// </returns>
    public static string? BaseUrlRefusal(string? rawBaseUrl)
        => CheckBaseUrl(rawBaseUrl, out _) is { Length: > 0 } reason ? reason : null;

    /// <summary>
    /// Tries to build the stream URL the Jellyfin server itself uses (relay origin, ffmpeg input):
    /// the base URL plus <see cref="PluginConfiguration.StreamPath"/>, which is always resolved
    /// under the base URL's path, on the same scheme, host, port and userinfo. A stream path that is itself an
    /// absolute URI (it has a scheme) is refused.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="url">The absolute stream URL, or null when the configuration is incomplete or escapes the base.</param>
    /// <returns>True when a usable URL was built.</returns>
    public static bool TryGetServerStreamUrl(PluginConfiguration config, out Uri? url)
    {
        ArgumentNullException.ThrowIfNull(config);
        url = null;

        if (!TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return false;
        }

        // Relative to the base: "/stream.mp3", "stream.mp3" and "//stream.mp3" all mean <base>/stream.mp3.
        var relative = (config.StreamPath ?? string.Empty).Trim().TrimStart('/');
        if (relative.Length == 0 || Array.Exists(relative.Split('/'), segment => segment == "..") || Uri.TryCreate(relative, UriKind.Absolute, out _))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, relative, out var built)
            || !IsHttp(built)
            // Load-bearing, not a backstop: "\/host:8000/x" is no absolute URI on Linux, yet resolves to the same host on another port.
            || Uri.Compare(built, baseUrl, UriComponents.SchemeAndServer | UriComponents.UserInfo, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0
            || !built.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        url = built;
        return true;
    }

    /// <summary>
    /// Tries to parse <see cref="PluginConfiguration.ClientStreamUrl"/> (Direct mode only). Any host is allowed:
    /// Direct mode hands this URL to clients on purpose. Userinfo is refused, so no credential is disclosed with it.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="url">The absolute client stream URL, or null.</param>
    /// <returns>
    /// True when a valid URL is configured; false when the field is empty (use the server URL)
    /// or set but not an absolute http(s) URL without userinfo (the caller should warn and use the server URL).
    /// </returns>
    public static bool TryGetClientStreamUrl(PluginConfiguration config, out Uri? url)
    {
        ArgumentNullException.ThrowIfNull(config);
        url = null;

        var raw = config.ClientStreamUrl?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) || !IsHttp(parsed) || HasUserInfo(parsed))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="PluginConfiguration.ClientStreamUrl"/> is set to something.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>True when the field is non-blank.</returns>
    public static bool HasClientStreamUrl(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(config.ClientStreamUrl);
    }

    /// <summary>
    /// The one base URL rule. Returns null when usable (with <paramref name="baseUrl"/> set), the empty string when
    /// blank, or the refusal reason.
    /// </summary>
    private static string? CheckBaseUrl(string? rawBaseUrl, out Uri? baseUrl)
    {
        baseUrl = null;

        var raw = rawBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // Check the value as typed, before the slash is appended: "http://host/radio?x=1" would otherwise carry "?x=1/".
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var typed))
        {
            return RefusedUnparsable;
        }

        if (!IsHttp(typed))
        {
            return RefusedScheme;
        }

        if (HasUserInfo(typed))
        {
            return RefusedCredentials;
        }

        if (typed.Query.Length > 0)
        {
            return RefusedQuery;
        }

        if (typed.Fragment.Length > 0)
        {
            return RefusedFragment;
        }

        if (!Uri.TryCreate(raw + "/", UriKind.Absolute, out var parsed) || !IsHttp(parsed))
        {
            return RefusedUnparsable;
        }

        baseUrl = parsed;
        return null;
    }

    private static bool IsHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // With the delimiter, so "http://@host/" counts too (its UserInfo is empty, but the "@" stays in the URL).
    private static bool HasUserInfo(Uri uri)
        => uri.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter, UriFormat.UriEscaped).Length > 0;
}
