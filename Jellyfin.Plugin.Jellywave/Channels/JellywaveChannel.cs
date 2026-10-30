using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Channels;

/// <summary>
/// The Live Radio tile as a Jellyfin channel: one tile in My Media, named by <see cref="PluginConfiguration.LibraryName"/>,
/// holding one live audio item per enabled station (card L2). An item's external id is its station's id, so Jellyfin keeps
/// one entity per station across refreshes.
/// </summary>
public class JellywaveChannel : IChannel
{
    /// <summary>
    /// Overview of the live item when nothing is known about what is on air.
    /// </summary>
    public const string DefaultOverview = "Live broadcast. One shared stream; everyone hears the same thing at the same time.";

    /// <summary>
    /// Name of a plain stream whose name is blank and whose stream URL has no host (card L11; such a station is not
    /// listed, so the name shows on the configuration page and in the log only).
    /// </summary>
    public const string UnnamedStreamName = "Unnamed stream";

    /// <summary>The tile's description when the primary SUB/WAVE station has no tagline (card L11: any kind of station).</summary>
    public const string DefaultDescription = "Live internet radio.";

    private const string FallbackStationName = ConfigMigration.LegacyFallbackTileName;
    private const string ItemNameSuffix = " — Live";
    private const string LogoResourceName = "Jellyfin.Plugin.Jellywave.Assets.logo.png";
    private const string ListenPagePath = "listen";
    private const int BitsPerKilobit = 1000;

    /// <summary>The warning-streak key for "no station is enabled".</summary>
    private const string NoStationsKey = "no-stations";

    /// <summary>The warning-streak key for "every enabled station was refused".</summary>
    private const string AllRefusedKey = "all-refused";

    /// <summary>
    /// Bump this date whenever the shape of the media source changes. Jellyfin only re-saves a channel
    /// item's media sources when the item's DateModified advances, so without it an upgraded plugin would
    /// keep serving the sources saved by the previous build until the admin re-saved the configuration.
    /// L2: one source per station, codec and container from the station, HLS in Relay only, URLs as escaped AbsoluteUri.
    /// L10: Relay only, so a station saved in Direct loses its direct-play source and its client URL.
    /// </summary>
    internal static readonly DateTime MediaSourceSchemaDate = new(2026, 10, 28, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Nominal runtime advertised on the media source. Jellyfin's audio HLS path has no live mode:
    /// a source without a runtime makes the master playlist point at Audio/{id}/live.m3u8, a route that
    /// only exists for video, so the web client fails before ffmpeg starts. With a runtime the server emits
    /// a normal segmented playlist and transcodes on demand, which works for a live input as long as the
    /// listener does not seek. The item itself still carries no runtime (IsLiveStream), so lists show "Live".
    /// </summary>
    private static readonly TimeSpan NominalRuntime = TimeSpan.FromHours(6);

    private readonly ILogger<JellywaveChannel> _logger;
    private readonly NowPlayingState _nowPlaying;
    private readonly IUserReader _users;
    private readonly RenameBridge _bridge;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly WarningStreaks _warnings = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveChannel"/> class (DI constructor). Built while Jellyfin
    /// constructs <c>ChannelManager</c>, which takes <c>IUserManager</c> and <c>ILibraryManager</c> before the channels
    /// (12.3), so the user reader and the bridge (through <see cref="LiveItemFinder"/>, which resolves the channels
    /// lazily) add no cycle.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="nowPlaying">The shared now-playing state.</param>
    /// <param name="users">Reads users for <see cref="IsEnabledFor"/>; read-only on purpose.</param>
    /// <param name="bridge">The rename bridge.</param>
    public JellywaveChannel(ILogger<JellywaveChannel> logger, NowPlayingState nowPlaying, IUserReader users, RenameBridge bridge)
        : this(logger, nowPlaying, users, bridge, () => Config)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveChannel"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="nowPlaying">The shared now-playing state.</param>
    /// <param name="users">Reads users for <see cref="IsEnabledFor"/>.</param>
    /// <param name="bridge">The rename bridge.</param>
    /// <param name="configuration">Returns the current configuration.</param>
    internal JellywaveChannel(ILogger<JellywaveChannel> logger, NowPlayingState nowPlaying, IUserReader users, RenameBridge bridge, Func<PluginConfiguration> configuration)
    {
        _logger = logger;
        _nowPlaying = nowPlaying;
        _users = users;
        _bridge = bridge;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string Name => TileName(_configuration());

    /// <inheritdoc />
    public string Description
        => _configuration().PrimaryStation() is { Tagline: { } tagline } && !string.IsNullOrWhiteSpace(tagline) ? tagline.Trim() : DefaultDescription;

    /// <inheritdoc />
    public string DataVersion => ComputeDataVersion(_configuration());

    /// <inheritdoc />
    public string HomePageUrl
        => StationUrls.TryGetBaseUrl(_configuration(), out var baseUrl) && baseUrl is not null
            ? new Uri(baseUrl, ListenPagePath).ToString()
            : string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = [ChannelMediaType.Audio],
            ContentTypes = [ChannelMediaContentType.Song],
            DefaultSortFields = [ChannelItemSortField.Name],
            MaxPageSize = 10,
            AutoRefreshLevels = 1
        };
    }

    /// <summary>
    /// The rename bridge's tile half: false when the user's block list names the id of an earlier tile name, so a user
    /// blocked from the old tile does not get the renamed one. Jellyfin 12.3 asks this after <c>Channel.IsVisible</c>
    /// for every channel listing (<c>ChannelManager.GetChannelsInternalAsync</c>), and My Media is built from that list.
    /// Reads the user only when there are earlier names. Fails closed: an id that is not an "N" guid, a missing user or
    /// any error hides the tile.
    /// </summary>
    /// <param name="userId">The user id, "N" format.</param>
    /// <returns>True when the tile may be listed for the user.</returns>
    public bool IsEnabledFor(string userId)
    {
        try
        {
            if (!Guid.TryParseExact(userId, "N", out var id) || id == Guid.Empty)
            {
                return false;
            }

            var previous = _bridge.PreviousIds(_configuration());
            if (previous.Count == 0)
            {
                return true;
            }

            return _users.GetUser(id) is { } user && !RenameBridge.BlocksAny(user, previous);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the previous tile names' block lists; the tile is hidden for this listing");
            return false;
        }
    }

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var config = _configuration();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = StationSources.Publish(config, (station, reason) => Warn(seen, station, reason));

        if (sources.Count == 0 && seen.Count == 0)
        {
            seen.Add(NoStationsKey);
            if (_warnings.Begin(NoStationsKey, NoStationsKey))
            {
                _logger.LogWarning("No station is enabled; the tile has no items until one is added under Dashboard > Plugins > Jellywave");
            }
        }
        else if (sources.Count == 0)
        {
            // Stations exist but every one was refused (each has its own warning above): say so once, not "none enabled".
            seen.Add(AllRefusedKey);
            if (_warnings.Begin(AllRefusedKey, AllRefusedKey))
            {
                _logger.LogWarning("Every enabled station was refused (see the warnings above); the tile has no items until one is fixed under Dashboard > Plugins > Jellywave");
            }
        }

        _warnings.Retain(seen);

        var items = new List<ChannelItemInfo>(sources.Count);
        foreach (var source in sources)
        {
            // Applied by Jellyfin only when it creates the item; the poller keeps it after that (card L4: every SUB/WAVE
            // station; card L8: a plain stream's notes).
            var overview = source.Key is { } key ? _nowPlaying.Snapshot(key).Description : StreamOverview(source.Station);
            items.Add(BuildLiveItem(config, source, overview));
        }

        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        var stream = OpenLogoStream();
        if (stream is null)
        {
            _logger.LogError("Embedded logo resource {Resource} is missing from the assembly", LogoResourceName);
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Format = ImageFormat.Png,
            Stream = stream
        });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];

    /// <summary>
    /// Opens the embedded station logo (PNG). Also used as the live item's cover when the station has no art.
    /// </summary>
    /// <returns>The stream, or null when the resource is missing from the assembly.</returns>
    public static Stream? OpenLogoStream() => typeof(JellywaveChannel).Assembly.GetManifestResourceStream(LogoResourceName);

    private static int PositiveOr(int value, int fallback) => value > 0 ? value : fallback;

    /// <summary>
    /// Gets the effective tile name: <see cref="PluginConfiguration.LibraryName"/>, or "Live Radio" when blank. Jellyfin
    /// derives the channel id from it.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The name used for the channel.</returns>
    public static string TileName(PluginConfiguration config) => ConfigMigration.TileName(config);

    /// <summary>
    /// Gets the effective name of the primary station: its name, or the fallback of <see cref="StationName(StationDefinition)"/>.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The station name for the live item and the station page.</returns>
    public static string StationName(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return StationName(config.PrimaryStation());
    }

    /// <summary>
    /// Gets the effective name of a station: its name; when blank, a plain stream is named after its stream URL's host in
    /// ASCII form (card L11, as the configuration page names it; "Unnamed stream" without a host), and a SUB/WAVE station
    /// or no station at all is "SUB/WAVE".
    /// </summary>
    /// <param name="station">The station, or null.</param>
    /// <returns>The station name.</returns>
    public static string StationName(StationDefinition? station)
    {
        if (station is null)
        {
            return FallbackStationName;
        }

        if (!string.IsNullOrWhiteSpace(station.Name))
        {
            return station.Name.Trim();
        }

        return station.Kind == StationKind.Stream ? StreamHost(station.StreamUrl) ?? UnnamedStreamName : FallbackStationName;
    }

    /// <summary>
    /// The host of an http or https URL in ASCII form (<see cref="Uri.IdnHost"/>, punycode for an international name, as
    /// the browser's <c>URL.hostname</c> gives it; an IPv6 literal keeps its brackets, as there), or null.
    /// </summary>
    /// <param name="url">The URL as stored.</param>
    /// <returns>The host, or null.</returns>
    internal static string? StreamHost(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return string.IsNullOrEmpty(host) ? null : host;
    }

    /// <summary>
    /// Gets the name of a station's item: "&lt;station name&gt; — Live". Jellyfin applies a channel item's name only when it
    /// creates the item (12.3 <c>ChannelManager.GetChannelItemEntityAsync</c>), so the poller writes it on a rename (card L3).
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The item name.</returns>
    public static string ItemName(StationDefinition? station)
        => StationName(station) + ItemNameSuffix;

    /// <summary>
    /// Gets a plain stream item's overview (card L8): the station's notes when set (so every client shows them in
    /// Jellyfin's own detail view), else <see cref="DefaultOverview"/>. The notes may come from a stream's
    /// <c>icy-description</c>, and jellyfin-web renders an overview as sanitized HTML and Markdown, so <c>&lt;</c> and
    /// <c>&gt;</c> are dropped here: no tag reaches the overview. The station page shows the notes as they are, as text.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The overview.</returns>
    public static string StreamOverview(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return OverviewText(station.Notes) ?? DefaultOverview;
    }

    /// <summary>
    /// Gets an idle SUB/WAVE station item's overview (card L14, HI Q2: the station's own look, not a stale track and not
    /// generic text): its tagline (the editor's "Description"), else its notes, else <see cref="DefaultOverview"/>. The
    /// tagline can come from the station (Check fills it from the DJ's tagline), so it is cleaned like the notes in
    /// <see cref="StreamOverview"/>: <c>&lt;</c> and <c>&gt;</c> dropped, at most <see cref="StationDefinition.MaxNotesLength"/>
    /// characters.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The overview.</returns>
    public static string IdleOverview(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return OverviewText(station.Tagline) ?? OverviewText(station.Notes) ?? DefaultOverview;
    }

    /// <summary>
    /// A station's own text as an item overview: cleaned (<see cref="StationText.Clean"/>), with <c>&lt;</c> and <c>&gt;</c>
    /// dropped, since jellyfin-web renders an overview as sanitized HTML and Markdown; null when nothing is left.
    /// </summary>
    private static string? OverviewText(string? text)
    {
        var cleaned = StationText.Clean(text, StationDefinition.MaxNotesLength)?.Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal);
        return StationText.Clean(cleaned, StationDefinition.MaxNotesLength);
    }

    private static DateTime ItemDateModified(PluginConfiguration config)
        => config.LastChangedUtc > MediaSourceSchemaDate ? config.LastChangedUtc : MediaSourceSchemaDate;

    /// <summary>
    /// Hashes everything the listed items are built from: the tile name, the item date, and every field of every enabled
    /// station in grid order (a disabled station changes nothing until it is enabled). Jellyfin caches channel item lists
    /// for three hours keyed by this value, so any change to a station lists the items again.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The data version.</returns>
    internal static string ComputeDataVersion(PluginConfiguration config)
    {
        var material = new List<string?[]>
        {
            new[] { TileName(config), ItemDateModified(config).ToString("O", CultureInfo.InvariantCulture) }
        };
        foreach (var station in StationSources.Enabled(config))
        {
            material.Add(
            [
                station.Id,
                station.Name,
                station.Kind.ToString(),
                station.Enabled.ToString(CultureInfo.InvariantCulture),
                station.SortOrder.ToString(CultureInfo.InvariantCulture),
                station.BaseUrl,
                station.StreamPath,
                station.StreamUrl,
                station.LogoUrl,
                station.Tagline,
                station.Genre,
                station.Notes,
                station.Website,
                station.Codec,
                station.Container,
                station.BitrateKbps.ToString(CultureInfo.InvariantCulture),
                station.SampleRate.ToString(CultureInfo.InvariantCulture),
                station.Channels.ToString(CultureInfo.InvariantCulture)
            ]);
        }

        // JSON keeps the fields apart unambiguously ("a|b" + "c" never equals "a" + "b|c").
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(material));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private void Warn(HashSet<string> seen, StationDefinition station, string reason)
    {
        // Keyed by position-independent identity; a station without a usable id is keyed by its name.
        var key = (StationClient.IsValidId(station.Id) ? "id:" + station.Id : "name:" + station.Name) + "\n" + reason;
        seen.Add(key);
        if (_warnings.Begin(key, reason))
        {
            _logger.LogWarning("Station {Name} is not listed: {Reason}; fix it under Dashboard > Plugins > Jellywave", StationName(station), reason);
        }
    }

    private static ChannelItemInfo BuildLiveItem(PluginConfiguration config, StationSource source, string? overview)
    {
        var station = source.Station;
        var name = StationName(station);

        var bitrate = PositiveOr(station.BitrateKbps, PluginConfiguration.DefaultStreamBitrateKbps) * BitsPerKilobit;
        var sampleRate = PositiveOr(station.SampleRate, PluginConfiguration.DefaultStreamSampleRate);
        var channels = PositiveOr(station.Channels, PluginConfiguration.DefaultStreamChannels);

        // Relay only (card L10): no client is offered direct play, so Jellyfin's ffmpeg reads Path (no EncoderPath) and
        // serves the client from the server. Path is still in the item's PlaybackInfo for anyone who can see the item.
        // AbsoluteUri, never ToString(): ToString() unescapes %22 to a quote, and Jellyfin 12.3 puts a remote input
        // between quotes without escaping it (EncodingUtils.GetInputArgument), so a quote could end the ffmpeg argument.
        var mediaSource = new MediaSourceInfo
        {
            Name = string.Format(CultureInfo.InvariantCulture, "{0} live stream", name),
            Path = source.ServerUrl.AbsoluteUri,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            IsInfiniteStream = true,
            Container = source.Container,
            Bitrate = bitrate,
            RunTimeTicks = NominalRuntime.Ticks,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = false,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Codec = source.Codec,
                    Index = 0,
                    IsDefault = true,
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitRate = bitrate
                }
            ]
        };

        return new ChannelItemInfo
        {
            Id = station.Id,
            Name = ItemName(station),
            Overview = overview ?? DefaultOverview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Audio,
            ContentType = ChannelMediaContentType.Song,
            IsLiveStream = true,
            DateModified = ItemDateModified(config),
            DateCreated = ItemDateModified(config),
            MediaSources = [mediaSource]
        };
    }
}
