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
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        StationBaseUrl = string.Empty;
        StreamPath = "/stream.mp3";
        ClientStreamUrl = string.Empty;
        StationName = "SUB/WAVE";
        Delivery = DeliveryMode.Relay;
        NowPlayingEnabled = true;
        NowPlayingPollSeconds = 20;
        LastChangedUtc = DateTime.UnixEpoch;
    }

    /// <summary>
    /// Gets or sets the SUB/WAVE base URL as seen from the Jellyfin server (scheme, host, port; no trailing slash).
    /// Empty until configured; the channel publishes no items while it is empty.
    /// </summary>
    public string StationBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the stream mount path appended to <see cref="StationBaseUrl"/>.
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
    /// Gets or sets how the audio is delivered to clients.
    /// </summary>
    public DeliveryMode Delivery { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the now-playing poller is enabled.
    /// </summary>
    public bool NowPlayingEnabled { get; set; }

    /// <summary>
    /// Gets or sets the now-playing poll interval in seconds (5..300).
    /// </summary>
    public int NowPlayingPollSeconds { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the configuration last changed. Drives channel cache invalidation.
    /// Defaults to the Unix epoch so an unsaved configuration is stable across restarts.
    /// </summary>
    public DateTime LastChangedUtc { get; set; }
}
