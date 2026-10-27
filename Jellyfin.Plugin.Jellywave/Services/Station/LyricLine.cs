using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>One lyric line.</summary>
public sealed class LyricLine
{
    /// <summary>Gets or sets the onset in milliseconds from the start of play, or null when unsynced.</summary>
    [JsonPropertyName("startMs")]
    public int? StartMs { get; set; }

    /// <summary>Gets or sets the text.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
