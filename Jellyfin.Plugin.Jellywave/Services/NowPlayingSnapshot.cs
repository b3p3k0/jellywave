using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// One consistent reading of <see cref="NowPlayingState"/>: the feed, the station state and the values derived from the
/// feed, all from the same station origin, taken under one lock. Callers must not modify the feed or the state.
/// </summary>
public sealed class NowPlayingSnapshot
{
    /// <summary>
    /// The reading of a state that has heard nothing (fresh start, station unreachable, or another station than the
    /// configured one).
    /// </summary>
    public static readonly NowPlayingSnapshot Nothing = new(null, null, null, DateTime.MinValue, null, null, null);

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingSnapshot"/> class.
    /// </summary>
    /// <param name="origin">The station origin the feed came from, or null.</param>
    /// <param name="feed">The feed, or null.</param>
    /// <param name="stationState">The station state, or null.</param>
    /// <param name="fetchedUtc">The time of the last successful fetch.</param>
    /// <param name="coverId">The cover id of the track on air, or null.</param>
    /// <param name="trackLine">The "Artist – Title" line, or null.</param>
    /// <param name="description">The one-line description, or null.</param>
    public NowPlayingSnapshot(string? origin, NowPlayingFeed? feed, StationState? stationState, DateTime fetchedUtc, string? coverId, string? trackLine, string? description)
    {
        Origin = origin;
        Feed = feed;
        StationState = stationState;
        FetchedUtc = fetchedUtc;
        CoverId = coverId;
        TrackLine = trackLine;
        Description = description;
    }

    /// <summary>Gets the station origin (<see cref="StationUrls.Origin"/>) the feed came from, or null when nothing is known.</summary>
    public string? Origin { get; }

    /// <summary>Gets the feed, or null.</summary>
    public NowPlayingFeed? Feed { get; }

    /// <summary>Gets the station state, or null.</summary>
    public StationState? StationState { get; }

    /// <summary>Gets the time of the last successful fetch, or <see cref="DateTime.MinValue"/>.</summary>
    public DateTime FetchedUtc { get; }

    /// <summary>Gets the cover id of the track on air (see <see cref="NowPlayingState.CoverId"/>), or null.</summary>
    public string? CoverId { get; }

    /// <summary>Gets the "Artist – Title" line (see <see cref="NowPlayingState.TrackLine"/>), or null.</summary>
    public string? TrackLine { get; }

    /// <summary>Gets the one-line description (see <see cref="NowPlayingState.Description"/>), or null.</summary>
    public string? Description { get; }
}
