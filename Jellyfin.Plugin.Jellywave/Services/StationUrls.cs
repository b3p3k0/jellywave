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
    /// <param name="baseUrl">The base URL, or null when unset or not an absolute http(s) URL.</param>
    /// <returns>True when the base URL is usable.</returns>
    public static bool TryGetBaseUrl(PluginConfiguration config, out Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(config);
        baseUrl = null;

        var raw = config.StationBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(raw))
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
    /// under the base URL's path and host.
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
        if (relative.Length == 0 || Array.Exists(relative.Split('/'), segment => segment == ".."))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, relative, out var built)
            || !IsHttp(built)
            || !string.Equals(built.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            || !built.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        url = built;
        return true;
    }

    /// <summary>
    /// Tries to parse <see cref="PluginConfiguration.ClientStreamUrl"/> (Direct mode only).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="url">The absolute client stream URL, or null.</param>
    /// <returns>
    /// True when a valid URL is configured; false when the field is empty (use the server URL)
    /// or set but not an absolute http(s) URL (the caller should warn and use the server URL).
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

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) || !IsHttp(parsed))
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
}
