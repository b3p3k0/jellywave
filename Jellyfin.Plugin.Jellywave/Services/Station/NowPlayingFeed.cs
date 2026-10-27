using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Subset of SUB/WAVE's <c>GET /api/now-playing</c>.
/// </summary>
public sealed class NowPlayingFeed
{
    /// <summary>Gets or sets the current track.</summary>
    [JsonPropertyName("nowPlaying")]
    public TrackInfo? NowPlaying { get; set; }

    /// <summary>Gets or sets the stream format block.</summary>
    [JsonPropertyName("stream")]
    public StationStreamInfo? Stream { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is online. Null when the feed did not say.</summary>
    [JsonPropertyName("streamOnline")]
    public bool? StreamOnline { get; set; }

    /// <summary>Gets or sets the DJ on air.</summary>
    [JsonPropertyName("dj")]
    public DjInfo? Dj { get; set; }

    /// <summary>Gets or sets the active show.</summary>
    [JsonPropertyName("activeShow")]
    public ShowInfo? ActiveShow { get; set; }

    /// <summary>Gets or sets the context block (next show).</summary>
    [JsonPropertyName("context")]
    public ContextInfo? Context { get; set; }
}
