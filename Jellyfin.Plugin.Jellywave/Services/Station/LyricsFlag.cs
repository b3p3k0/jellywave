using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Whether the station offers lyrics (<c>state.lyrics</c>).</summary>
public sealed class LyricsFlag
{
    /// <summary>Gets or sets a value indicating whether lyrics are enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
