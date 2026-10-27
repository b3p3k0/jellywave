using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>The track a listener request resolved to.</summary>
public sealed class RequestTrack
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }
}
