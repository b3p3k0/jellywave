using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Answer to <c>GET /api/lyrics/current</c>.</summary>
public sealed class LyricsPayload
{
    /// <summary>Gets or sets the song id.</summary>
    [JsonPropertyName("songId")]
    public string? SongId { get; set; }

    /// <summary>Gets or sets a value indicating whether the lines carry timings.</summary>
    [JsonPropertyName("synced")]
    public bool? Synced { get; set; }

    /// <summary>Gets or sets the lines.</summary>
    [JsonPropertyName("lines")]
    public IReadOnlyList<LyricLine>? Lines { get; set; }

    /// <summary>Gets or sets a value indicating whether the station is still looking the lyrics up.</summary>
    [JsonPropertyName("pending")]
    public bool? Pending { get; set; }

    /// <summary>Gets or sets the station's error text.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
