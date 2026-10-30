using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Turns the configured stations into the items the tile lists (card L2). Pure functions, no I/O. A station that fails
/// its rules is left out with a fixed reason that never repeats its URL; the others are listed regardless.
/// </summary>
public static class StationSources
{
    /// <summary>The container of an HLS station's media source (what Jellyfin's own probe reports for HLS input).</summary>
    public const string HlsContainer = "hls";

    /// <summary>Refusal: the id cannot be an item's external id.</summary>
    public const string RefusedId = "its id is missing or is not 1 to 64 letters, digits, _ or -";

    /// <summary>Refusal: an earlier station in the list already has the id.</summary>
    public const string RefusedDuplicateId = "an earlier station in the list has the same id";

    /// <summary>Refusal: the kind is neither SubWave nor Stream.</summary>
    public const string RefusedKind = "its type is not SUB/WAVE or Stream";

    /// <summary>Refusal: a SUB/WAVE station without a usable base URL and stream path.</summary>
    public const string RefusedSubWaveUrl = "its base URL or stream path is not set, or the stream path leaves the base URL";

    /// <summary>Refusal prefix: a SUB/WAVE station whose base URL is set but refused (followed by the card 14 reason).</summary>
    public const string RefusedBaseUrlPrefix = "its base URL is not allowed: ";

    /// <summary>Refusal: a plain stream without a stream URL.</summary>
    public const string RefusedNoStreamUrl = "its stream URL is not set";

    /// <summary>Refusal prefix: a plain stream whose URL is refused (followed by the whole-URL rule's reason).</summary>
    public const string RefusedStreamUrlPrefix = "its stream URL is not allowed: ";

    /// <summary>Refusal: the stream URL names a playlist file, which ffmpeg cannot open (card L0).</summary>
    public const string RefusedPlaylist = "its stream URL is a .pls or .m3u playlist; use the stream address the playlist lists";

    /// <summary>Refusal: the stream URL names a DASH manifest, which the plugin does not serve.</summary>
    public const string RefusedDash = "its stream URL is a DASH manifest (.mpd), which Jellywave does not serve";

    /// <summary>Refusal: the codec is not one the plugin serves.</summary>
    public const string RefusedCodec = "its codec is not mp3 or aac";

    /// <summary>Refusal: the container is not one the plugin serves.</summary>
    public const string RefusedContainer = "its container is not mp3, aac or hls";

    /// <summary>Not listed: the station is disabled (the configuration page's status; <see cref="Publish"/> skips it silently).</summary>
    public const string NotListedDisabled = "it is disabled";

    private const string Mp3 = "mp3";
    private const string Aac = "aac";
    private const string M3u8Alias = "m3u8";
    private const string HlsExtension = ".m3u8";

    private const string DashExtension = ".mpd";

    // L0: ffmpeg cannot open these playlist files; the add-station probe (L3) resolves them to the stream they list.
    private static readonly string[] PlaylistExtensions = [".pls", ".m3u"];

    /// <summary>
    /// Gets the enabled stations in grid order: by <see cref="StationDefinition.SortOrder"/>, list order breaking ties.
    /// Null entries are skipped. These are the stations whose fields decide what the tile lists.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The enabled stations, ordered.</returns>
    public static IReadOnlyList<StationDefinition> Enabled(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.Stations ?? []).Where(s => s is not null && s.Enabled).OrderBy(s => s.SortOrder).ToList();
    }

    /// <summary>
    /// Builds the listed stations: every enabled station that passes its rules, in grid order, ranked from 0.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="refused">Called with each station left out and the fixed reason; optional.</param>
    /// <returns>The listed stations.</returns>
    public static IReadOnlyList<StationSource> Publish(PluginConfiguration config, Action<StationDefinition, string>? refused = null)
    {
        var sources = new List<StationSource>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in Enabled(config))
        {
            var reason = Check(station, out var serverUrl, out var codec, out var container, out var isHls);
            if (reason is null && !ids.Add(station.Id))
            {
                // Case-insensitive: Jellyfin matches a channel item's external id ignoring case (ChannelManager, 12.3).
                reason = RefusedDuplicateId;
            }

            if (reason is not null || serverUrl is null)
            {
                refused?.Invoke(station, reason ?? RefusedSubWaveUrl);
                continue;
            }

            // Check passed, so a SUB/WAVE station's base URL parses (TryGetServerStreamUrl parsed it the same way).
            var baseUrl = station.Kind == StationKind.SubWave && StationUrls.TryParseBaseUrl(station.BaseUrl, out var stationBase) ? stationBase : null;

            sources.Add(new StationSource(station, sources.Count, serverUrl, codec, container, isHls, baseUrl));
        }

        return sources;
    }

    /// <summary>
    /// Gets the listed source of the primary SUB/WAVE station: the <see cref="PluginConfiguration.PrimaryCandidate"/>, and
    /// only while <see cref="Publish"/> lists it. A station route called without a station id serves it (card L5: an
    /// older cached page sends none).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The source, or null when there is no candidate or it is not listed.</returns>
    public static StationSource? Primary(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var candidate = config.PrimaryCandidate();
        return candidate is null ? null : Publish(config).FirstOrDefault(s => ReferenceEquals(s.Station, candidate));
    }

    /// <summary>
    /// Says why a station of this configuration is not listed, duplicate ids included (for the configuration page).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="station">A station of <paramref name="config"/>.</param>
    /// <returns>The fixed reason, or null when it is listed or not enabled.</returns>
    public static string? NotListedReason(PluginConfiguration config, StationDefinition station)
    {
        string? reason = null;
        Publish(config, (refused, why) => reason = ReferenceEquals(refused, station) ? why : reason);
        return reason;
    }

    /// <summary>
    /// Says why one station cannot be listed, without looking at the other stations (duplicate ids are
    /// <see cref="Publish"/>'s check). Null when it can.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The fixed reason, or null.</returns>
    public static string? Refusal(StationDefinition station) => Check(station, out _, out _, out _, out _);

    private static string? Check(StationDefinition station, out Uri? serverUrl, out string codec, out string container, out bool isHls)
    {
        ArgumentNullException.ThrowIfNull(station);
        serverUrl = null;
        codec = Mp3;
        container = Mp3;
        isHls = false;

        if (!StationClient.IsValidId(station.Id))
        {
            return RefusedId;
        }

        var urlRefusal = station.Kind switch
        {
            StationKind.SubWave => SubWaveUrl(station, out serverUrl),
            StationKind.Stream => StreamUrl(station, out serverUrl),
            _ => RefusedKind
        };
        if (urlRefusal is not null)
        {
            return urlRefusal;
        }

        var hlsPath = serverUrl!.AbsolutePath.EndsWith(HlsExtension, StringComparison.OrdinalIgnoreCase);
        codec = Normalize(station.Codec) ?? Mp3;
        if (codec is not (Mp3 or Aac))
        {
            return RefusedCodec;
        }

        container = Normalize(station.Container) switch
        {
            null => hlsPath ? HlsContainer : codec,
            M3u8Alias => HlsContainer,
            var given => given
        };

        // A .m3u8 address is an HLS playlist whatever the container says (Jellyfin's own M3U tuner reads the
        // extension the same way, M3UTunerHost.IsManifest).
        if (hlsPath)
        {
            container = HlsContainer;
        }

        if (container is not (Mp3 or Aac or HlsContainer))
        {
            return RefusedContainer;
        }

        isHls = container == HlsContainer;
        return null;
    }

    private static string? SubWaveUrl(StationDefinition station, out Uri? serverUrl)
    {
        if (StationUrls.TryGetServerStreamUrl(station, out serverUrl) && serverUrl is not null)
        {
            return null;
        }

        return StationUrls.BaseUrlRefusal(station.BaseUrl) is { } refusal ? RefusedBaseUrlPrefix + refusal : RefusedSubWaveUrl;
    }

    private static string? StreamUrl(StationDefinition station, out Uri? serverUrl)
    {
        if (StationUrls.TryParseStreamUrl(station.StreamUrl, out serverUrl) && serverUrl is not null)
        {
            // The path only, so a query such as "?f=.pls" does not count (Jellyfin's M3UTunerHost.IsManifest reads it the same way).
            var path = serverUrl.AbsolutePath;
            if (Array.Exists(PlaylistExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return RefusedPlaylist;
            }

            return path.EndsWith(DashExtension, StringComparison.OrdinalIgnoreCase) ? RefusedDash : null;
        }

        return StationUrls.StreamUrlRefusal(station.StreamUrl) is { } refusal ? RefusedStreamUrlPrefix + refusal : RefusedNoStreamUrl;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
