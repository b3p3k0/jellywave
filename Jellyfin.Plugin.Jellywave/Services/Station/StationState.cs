using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Subset of SUB/WAVE's <c>GET /api/state</c>.
/// </summary>
public sealed class StationState
{
    /// <summary>Gets or sets the station block.</summary>
    [JsonPropertyName("station")]
    public StationInfo? Station { get; set; }

    /// <summary>Gets or sets the privacy block.</summary>
    [JsonPropertyName("privacy")]
    public PrivacyInfo? Privacy { get; set; }

    /// <summary>Gets or sets the recently played tracks, newest first.</summary>
    [JsonPropertyName("history")]
    public IReadOnlyList<QueueTrack>? History { get; set; }

    /// <summary>Gets or sets the queued tracks, next first.</summary>
    [JsonPropertyName("upcoming")]
    public IReadOnlyList<QueueTrack>? Upcoming { get; set; }

    /// <summary>Gets or sets the lyrics flag.</summary>
    [JsonPropertyName("lyrics")]
    public LyricsFlag? Lyrics { get; set; }
}
