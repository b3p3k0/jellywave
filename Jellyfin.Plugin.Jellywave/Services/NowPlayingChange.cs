using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What changed between two now-playing feeds.
/// </summary>
[Flags]
public enum NowPlayingChange
{
    /// <summary>Nothing the live item shows has changed.</summary>
    None = 0,

    /// <summary>The description line or the track line changed.</summary>
    Description = 1,

    /// <summary>The cover art id changed.</summary>
    Cover = 2
}
