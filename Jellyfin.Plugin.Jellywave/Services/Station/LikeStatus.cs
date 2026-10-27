using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Answer to <c>GET /api/like</c> and <c>POST /api/like</c>.</summary>
public sealed class LikeStatus
{
    /// <summary>Gets or sets a value indicating whether likes are enabled on the station.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the like was recorded (POST).</summary>
    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    /// <summary>Gets or sets the likeable song id.</summary>
    [JsonPropertyName("songId")]
    public string? SongId { get; set; }

    /// <summary>Gets or sets a value indicating whether this caller already liked it.</summary>
    [JsonPropertyName("liked")]
    public bool? Liked { get; set; }

    /// <summary>Gets or sets the like count.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    /// <summary>Gets or sets the station's error text (403, 409, 429, 500).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Gets or sets the seconds to wait (429).</summary>
    [JsonPropertyName("retryAfter")]
    public int? RetryAfter { get; set; }
}
