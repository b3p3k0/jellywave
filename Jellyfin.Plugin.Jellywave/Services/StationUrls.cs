using System;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Builds the station URLs from configuration. Pure functions, no I/O.
/// </summary>
public static class StationUrls
{
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
    {
        baseUrl = null;

        var raw = rawBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Check the value as typed, before the slash is appended: "http://host/radio?x=1" would otherwise carry "?x=1/".
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var typed) || !IsHttp(typed) || HasUserInfo(typed) || typed.Query.Length > 0 || typed.Fragment.Length > 0)
        {
            return false;
        }

        if (!Uri.TryCreate(raw + "/", UriKind.Absolute, out var parsed) || !IsHttp(parsed))
        {
            return false;
        }

        baseUrl = parsed;
        return true;
    }

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

    private static bool IsHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // With the delimiter, so "http://@host/" counts too (its UserInfo is empty, but the "@" stays in the URL).
    private static bool HasUserInfo(Uri uri)
        => uri.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter, UriFormat.UriEscaped).Length > 0;
}
