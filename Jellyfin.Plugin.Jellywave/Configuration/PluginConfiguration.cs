using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// How the audio reaches the client.
/// </summary>
public enum DeliveryMode
{
    /// <summary>
    /// Jellyfin fetches the stream from the station and serves it to the client (ffmpeg relay).
    /// Works for every client that can reach Jellyfin, even when it cannot reach the station.
    /// </summary>
    Relay = 0,

    /// <summary>
    /// The client fetches the stream URL itself. Requires every client to reach the station host.
    /// </summary>
    Direct = 1
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Smallest allowed now-playing poll interval, in seconds.
    /// </summary>
    public const int MinPollSeconds = 5;

    /// <summary>
    /// Largest allowed now-playing poll interval, in seconds.
    /// </summary>
    public const int MaxPollSeconds = 300;

    /// <summary>
    /// Default stream bitrate in kbit/s when the station has not been queried (SUB/WAVE's default MP3 mount).
    /// </summary>
    public const int DefaultStreamBitrateKbps = 192;

    /// <summary>
    /// Default stream sample rate in Hz.
    /// </summary>
    public const int DefaultStreamSampleRate = 44100;

    /// <summary>
    /// Default stream channel count.
    /// </summary>
    public const int DefaultStreamChannels = 2;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        StationBaseUrl = string.Empty;
        StreamPath = "/stream.mp3";
        ClientStreamUrl = string.Empty;
        StationName = "SUB/WAVE";
        StationTagline = string.Empty;
        Delivery = DeliveryMode.Relay;
        NowPlayingEnabled = true;
        CoverArtEnabled = true;
        NowPlayingPollSeconds = 20;
        StreamBitrateKbps = DefaultStreamBitrateKbps;
        StreamSampleRate = DefaultStreamSampleRate;
        StreamChannels = DefaultStreamChannels;
        LastChangedUtc = DateTime.UnixEpoch;
    }

    /// <summary>
    /// Gets or sets the SUB/WAVE base URL as seen from the Jellyfin server (scheme, host, port, optional path prefix; no trailing slash).
    /// Empty until configured; the channel publishes no items while it is empty.
    /// </summary>
    public string StationBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the stream mount path, resolved under <see cref="StationBaseUrl"/>.
    /// </summary>
    public string StreamPath { get; set; }

    /// <summary>
    /// Gets or sets the stream URL as seen from clients. Only used in <see cref="DeliveryMode.Direct"/>.
    /// Empty means <see cref="StationBaseUrl"/> + <see cref="StreamPath"/>.
    /// </summary>
    public string ClientStreamUrl { get; set; }

    /// <summary>
    /// Gets or sets the name shown on the My Media tile. Renaming creates a new channel entry.
    /// </summary>
    public string StationName { get; set; }

    /// <summary>
    /// Gets or sets the station tagline shown as the channel description.
    /// </summary>
    public string StationTagline { get; set; }

    /// <summary>
    /// Gets or sets how the audio is delivered to clients.
    /// </summary>
    public DeliveryMode Delivery { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the now-playing poller is enabled.
    /// </summary>
    public bool NowPlayingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the poller saves the current track's cover art as the live item's image.
    /// Off, or when the station has no art for a track, the item shows the station logo.
    /// </summary>
    public bool CoverArtEnabled { get; set; }

    /// <summary>
    /// Gets or sets the now-playing poll interval in seconds. Read through <see cref="EffectivePollSeconds"/>.
    /// </summary>
    public int NowPlayingPollSeconds { get; set; }

    /// <summary>
    /// Gets or sets the stream bitrate in kbit/s, as reported by the station (fetch button) or left at the default.
    /// </summary>
    public int StreamBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the stream sample rate in Hz.
    /// </summary>
    public int StreamSampleRate { get; set; }

    /// <summary>
    /// Gets or sets the stream channel count.
    /// </summary>
    public int StreamChannels { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the configuration last changed. Drives channel cache invalidation.
    /// Defaults to the Unix epoch so an unsaved configuration is stable across restarts.
    /// </summary>
    public DateTime LastChangedUtc { get; set; }

    /// <summary>
    /// Gets the poll interval clamped to the allowed range.
    /// </summary>
    public int EffectivePollSeconds => Math.Clamp(NowPlayingPollSeconds, MinPollSeconds, MaxPollSeconds);
}
