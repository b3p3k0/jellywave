using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// The SUB/WAVE station as a Jellyfin channel: one tile in My Media holding one live audio item.
/// </summary>
public class JellywaveChannel : IChannel
{
    /// <summary>
    /// External id of the single live item. Stable, so Jellyfin keeps one entity across refreshes.
    /// </summary>
    public const string LiveItemId = "live";

    /// <summary>
    /// Overview of the live item when nothing is known about what is on air.
    /// </summary>
    public const string DefaultOverview = "Live broadcast. One shared stream; everyone hears the same thing at the same time.";

    private const string FallbackStationName = "SUB/WAVE";
    private const string LogoResourceName = "Jellyfin.Plugin.Jellywave.Assets.logo.png";
    private const string ListenPagePath = "listen";
    private const string StreamContainer = "mp3";
    private const int BitsPerKilobit = 1000;

    /// <summary>
    /// Nominal runtime advertised on the media source. Jellyfin's audio HLS path has no live mode:
    /// a source without a runtime makes the master playlist point at Audio/{id}/live.m3u8, a route that
    /// only exists for video, so the web client fails before ffmpeg starts. With a runtime the server emits
    /// a normal segmented playlist and transcodes on demand, which works for a live input as long as the
    /// listener does not seek. The item itself still carries no runtime (IsLiveStream), so lists show "Live".
    /// </summary>
    private static readonly TimeSpan NominalRuntime = TimeSpan.FromHours(6);

    /// <summary>
    /// Bump this date whenever the shape of the media source changes. Jellyfin only re-saves a channel
    /// item's media sources when the item's DateModified advances, so without it an upgraded plugin would
    /// keep serving the sources saved by the previous build until the admin re-saved the configuration.
    /// </summary>
    private static readonly DateTime MediaSourceSchemaDate = new(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc);

    private readonly ILogger<JellywaveChannel> _logger;
    private readonly NowPlayingState _nowPlaying;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveChannel"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="nowPlaying">The shared now-playing state.</param>
    public JellywaveChannel(ILogger<JellywaveChannel> logger, NowPlayingState nowPlaying)
    {
        _logger = logger;
        _nowPlaying = nowPlaying;
    }

    /// <inheritdoc />
    public string Name => StationName(Config);

    /// <inheritdoc />
    public string Description
        => string.IsNullOrWhiteSpace(Config.StationTagline) ? "Live internet radio from a SUB/WAVE station." : Config.StationTagline.Trim();

    /// <inheritdoc />
    public string DataVersion => ComputeDataVersion(Config);

    /// <inheritdoc />
    public string HomePageUrl
        => StationUrls.TryGetBaseUrl(Config, out var baseUrl) && baseUrl is not null
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

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var config = Config;

        if (!StationUrls.TryGetServerStreamUrl(config, out var serverUrl) || serverUrl is null)
        {
            _logger.LogWarning("Station base URL or stream path is not configured, not http(s), or the path escapes the base URL; the channel has no items until Dashboard > Plugins > Jellywave is set");
            return Task.FromResult(new ChannelItemResult { Items = [], TotalRecordCount = 0 });
        }

        var item = BuildLiveItem(config, serverUrl, ResolveClientUrl(config, serverUrl), _nowPlaying.Description());
        return Task.FromResult(new ChannelItemResult { Items = [item], TotalRecordCount = 1 });
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
    /// Gets the effective station name: the configured one, or a fallback when blank.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The name used for the channel.</returns>
    public static string StationName(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.StationName) ? FallbackStationName : config.StationName.Trim();
    }

    private static DateTime ItemDateModified(PluginConfiguration config)
        => config.LastChangedUtc > MediaSourceSchemaDate ? config.LastChangedUtc : MediaSourceSchemaDate;

    internal static string ComputeDataVersion(PluginConfiguration config)
    {
        var material = string.Join(
            '|',
            config.StationBaseUrl,
            config.StreamPath,
            config.ClientStreamUrl,
            config.Delivery.ToString(),
            StationName(config),
            config.StationTagline,
            config.StreamBitrateKbps.ToString(CultureInfo.InvariantCulture),
            config.StreamSampleRate.ToString(CultureInfo.InvariantCulture),
            config.StreamChannels.ToString(CultureInfo.InvariantCulture),
            ItemDateModified(config).ToString("O", CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private Uri ResolveClientUrl(PluginConfiguration config, Uri serverUrl)
    {
        if (config.Delivery != DeliveryMode.Direct)
        {
            return serverUrl;
        }

        if (StationUrls.TryGetClientStreamUrl(config, out var clientUrl) && clientUrl is not null)
        {
            return clientUrl;
        }

        if (StationUrls.HasClientStreamUrl(config))
        {
            _logger.LogWarning("Client stream URL is set but is not an absolute http(s) URL; Direct mode is using the server URL instead");
        }

        return serverUrl;
    }

    private static ChannelItemInfo BuildLiveItem(PluginConfiguration config, Uri serverUrl, Uri clientUrl, string? overview)
    {
        var name = StationName(config);
        var direct = config.Delivery == DeliveryMode.Direct;

        var bitrate = PositiveOr(config.StreamBitrateKbps, PluginConfiguration.DefaultStreamBitrateKbps) * BitsPerKilobit;
        var sampleRate = PositiveOr(config.StreamSampleRate, PluginConfiguration.DefaultStreamSampleRate);
        var channels = PositiveOr(config.StreamChannels, PluginConfiguration.DefaultStreamChannels);

        var mediaSource = new MediaSourceInfo
        {
            Name = string.Format(CultureInfo.InvariantCulture, "{0} live stream", name),
            Path = clientUrl.ToString(),
            Protocol = MediaProtocol.Http,
            // ffmpeg reads from the server-side URL when a Direct client still needs a transcode.
            EncoderPath = direct ? serverUrl.ToString() : null,
            EncoderProtocol = direct ? MediaProtocol.Http : null,
            IsRemote = true,
            IsInfiniteStream = true,
            Container = StreamContainer,
            Bitrate = bitrate,
            RunTimeTicks = NominalRuntime.Ticks,
            SupportsDirectPlay = direct,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = false,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Codec = StreamContainer,
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
            Id = LiveItemId,
            Name = string.Format(CultureInfo.InvariantCulture, "{0} — Live", name),
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
