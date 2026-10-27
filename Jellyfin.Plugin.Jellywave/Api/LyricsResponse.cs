using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>Lyrics of the track on air for the station page.</summary>
public sealed class LyricsResponse
{
    /// <summary>Gets or sets the song id the lines belong to.</summary>
    public string? SongId { get; set; }

    /// <summary>Gets or sets the status: ok, none, pending, off, unavailable, ended.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the lines carry timings.</summary>
    public bool Synced { get; set; }

    /// <summary>Gets or sets the lines in order.</summary>
    public IReadOnlyList<string> Lines { get; set; } = [];

    /// <summary>Gets or sets a human line for the non-ok statuses.</summary>
    public string? Message { get; set; }
}
