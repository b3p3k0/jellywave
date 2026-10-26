using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Current track.</summary>
public sealed class TrackInfo
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets the music server id of the track.</summary>
    [JsonPropertyName("subsonic_id")]
    public string? SubsonicId { get; set; }
}
