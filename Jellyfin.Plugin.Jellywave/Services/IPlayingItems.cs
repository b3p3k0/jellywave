using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The items Jellyfin's sessions are playing now (card L6): the narrow view <see cref="TitleSampler"/> needs to sample only
/// the plain streams someone listens to, and <see cref="StationDemand"/> to ask only the SUB/WAVE stations someone listens
/// to (card L14). Registered as <see cref="SessionPlayingItems"/>.
/// </summary>
public interface IPlayingItems
{
    /// <summary>
    /// Raised when a session starts playing an item (card L14), so an idle SUB/WAVE station is asked at once instead of
    /// after the poll interval.
    /// </summary>
    event EventHandler<PlaybackStartedEventArgs>? PlaybackStarted;

    /// <summary>Gets the ids of the items the sessions are playing (paused included).</summary>
    /// <returns>The item ids; empty when nothing plays.</returns>
    IReadOnlySet<Guid> PlayingItemIds();

    /// <summary>
    /// Gets the ids of the items played by the sessions with a user that <paramref name="userCounts"/> accepts (card L14
    /// fix round: only a user who can see the tile keeps a SUB/WAVE station asked).
    /// </summary>
    /// <param name="userCounts">Whether a session's user counts; a session counts when its user or an added user does.</param>
    /// <returns>The item ids; empty when nothing plays.</returns>
    IReadOnlySet<Guid> PlayingItemIds(Func<Guid, bool> userCounts);
}
