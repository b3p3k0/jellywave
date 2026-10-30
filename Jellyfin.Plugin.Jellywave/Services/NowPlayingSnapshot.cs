using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// One consistent reading of one station's entry in <see cref="NowPlayingState"/>: the feed, the station state and the
/// values derived from the feed, all from the same station key, taken under one lock. Callers must not modify the feed
/// or the state.
/// </summary>
public sealed class NowPlayingSnapshot
{
    /// <summary>
    /// The reading of a station nothing is known about: a fresh start, polling off, or the station unreachable or not
    /// polled.
    /// </summary>
    public static readonly NowPlayingSnapshot Nothing = new(null, null, DateTime.MinValue, null, null, null, false);

    /// <summary>
    /// The reading of a station whose entry holds data from another origin: its base URL changed since the last poll.
    /// Nothing is served from it; <see cref="OtherOrigin"/> tells the caller that the station's item may still show the
    /// previous origin's cover.
    /// </summary>
    public static readonly NowPlayingSnapshot OtherStation = new(null, null, DateTime.MinValue, null, null, null, true);

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingSnapshot"/> class.
    /// </summary>
    /// <param name="feed">The feed, or null.</param>
    /// <param name="stationState">The station state, or null.</param>
    /// <param name="fetchedUtc">The time of the last successful fetch.</param>
    /// <param name="coverId">The cover id of the track on air, or null.</param>
    /// <param name="trackLine">The "Artist – Title" line, or null.</param>
    /// <param name="description">The one-line description, or null.</param>
    /// <param name="otherOrigin">Whether the entry held another origin's data (see <see cref="OtherStation"/>).</param>
    public NowPlayingSnapshot(NowPlayingFeed? feed, StationState? stationState, DateTime fetchedUtc, string? coverId, string? trackLine, string? description, bool otherOrigin)
    {
        Feed = feed;
        StationState = stationState;
        FetchedUtc = fetchedUtc;
        CoverId = coverId;
        TrackLine = trackLine;
        Description = description;
        OtherOrigin = otherOrigin;
    }

    /// <summary>Gets the feed, or null.</summary>
    public NowPlayingFeed? Feed { get; }

    /// <summary>Gets the station state, or null.</summary>
    public StationState? StationState { get; }

    /// <summary>Gets the time of the last successful fetch, or <see cref="DateTime.MinValue"/>.</summary>
    public DateTime FetchedUtc { get; }

    /// <summary>
    /// Gets the station id of the current track's cover art, or null when there is no track, the stream is reported off
    /// air, or the id is not a valid station id (the id for <see cref="StationClient.GetCoverAsync"/>).
    /// </summary>
    public string? CoverId { get; }

    /// <summary>Gets the short "Artist – Title" line for the playing bar, or null when nothing is on air.</summary>
    public string? TrackLine { get; }

    /// <summary>
    /// Gets the one-line description of what is on air, for example "Now playing: Artist – Title (Album) · DJ Bobby ·
    /// The Ville", or null when nothing is known.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets a value indicating whether the station's entry held another origin's data, so nothing was served from it.
    /// </summary>
    public bool OtherOrigin { get; }
}
