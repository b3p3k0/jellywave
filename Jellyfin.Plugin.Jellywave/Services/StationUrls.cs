using System;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Builds the station URLs from configuration. Pure functions, no I/O.
/// </summary>
public static class StationUrls
{
    /// <summary>
    /// Tries to build the stream URL the Jellyfin server itself uses (relay origin, ffmpeg input).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="url">The absolute stream URL, or null when the configuration is incomplete.</param>
    /// <returns>True when a usable URL was built.</returns>
    public static bool TryGetServerStreamUrl(PluginConfiguration config, out Uri? url)
    {
        ArgumentNullException.ThrowIfNull(config);
        url = null;

        if (!TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return false;
        }

        var path = string.IsNullOrWhiteSpace(config.StreamPath) ? "/" : config.StreamPath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return Uri.TryCreate(baseUrl, path, out url);
    }

    /// <summary>
    /// Gets the stream URL clients fetch in <see cref="DeliveryMode.Direct"/>:
    /// <see cref="PluginConfiguration.ClientStreamUrl"/> when set, otherwise the server URL.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="serverUrl">The server-side stream URL already built.</param>
    /// <returns>The absolute client stream URL.</returns>
    public static Uri GetClientStreamUrl(PluginConfiguration config, Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(serverUrl);

        if (!string.IsNullOrWhiteSpace(config.ClientStreamUrl)
            && Uri.TryCreate(config.ClientStreamUrl.Trim(), UriKind.Absolute, out var clientUrl)
            && IsHttp(clientUrl))
        {
            return clientUrl;
        }

        return serverUrl;
    }

    /// <summary>
    /// Tries to build the station base URL (scheme, host, port, optional path prefix) with a trailing slash.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="baseUrl">The base URL, or null when unset or invalid.</param>
    /// <returns>True when the base URL is an absolute http(s) URL.</returns>
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

    private static bool IsHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
