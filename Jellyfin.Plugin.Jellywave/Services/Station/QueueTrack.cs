using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>A track in the station's history or upcoming lists (<c>GET /api/state</c>).</summary>
public sealed class QueueTrack
{
    /// <summary>Gets or sets the music server id.</summary>
    [JsonPropertyName("subsonic_id")]
    public string? SubsonicId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets who requested it, when it was a request.</summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }
}
