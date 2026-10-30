using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Who can see the tile right now (card L13), for the configuration page's "Who can see it": a count by the grant
/// button's rule, and the names of the users among them who cannot play a station. Read-only; changes no policy.
/// </summary>
public sealed class TileViewers
{
    /// <summary>Gets or sets how many active users there are (disabled users are left out, fix round).</summary>
    public int Users { get; set; }

    /// <summary>Gets or sets how many of the active users can see the tile (<see cref="AccessGranter.Standing.Visible"/>).</summary>
    public int Visible { get; set; }

    /// <summary>
    /// Gets or sets the names of the users who can see the tile but have "Allow audio playback that requires
    /// transcoding" (<c>EnableAudioPlaybackTranscoding</c>) off: every station is relayed through ffmpeg, so they cannot
    /// play any (card L10). Sorted, case ignored.
    /// </summary>
    public IReadOnlyList<string> NoAudioTranscoding { get; set; } = [];

    /// <summary>Gets or sets a short reason when nothing was counted (not ready, no tile yet, a name collision); null otherwise.</summary>
    public string? Error { get; set; }
}
