using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The latest now-playing snapshot, shared between the poller and the channel. Thread-safe.
/// </summary>
public sealed class NowPlayingState
{
    /// <summary>
    /// Longest station-supplied field kept in the description (the rest is cut with an ellipsis).
    /// </summary>
    public const int MaxFieldLength = 120;

    private readonly object _gate = new();
    private NowPlayingFeed? _feed;
    private StationState? _stationState;
    private DateTime _fetchedUtc;

    /// <summary>
    /// Gets the time of the last successful fetch, or <see cref="DateTime.MinValue"/>.
    /// </summary>
    public DateTime FetchedUtc
    {
        get
        {
            lock (_gate)
            {
                return _fetchedUtc;
            }
        }
    }

    /// <summary>
    /// Stores a fresh feed and reports what the live item would show differently.
    /// </summary>
    /// <param name="feed">The feed just fetched.</param>
    /// <param name="nowUtc">The fetch time.</param>
    /// <returns>Which of the description, track line and cover id differ from the previous feed.</returns>
    public NowPlayingChange Update(NowPlayingFeed feed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(feed);
        lock (_gate)
        {
            var textBefore = Describe(_feed) + "\n" + TrackLineOf(_feed);
            var coverBefore = CoverIdOf(_feed);
            _feed = feed;
            _fetchedUtc = nowUtc;

            var change = NowPlayingChange.None;
            if (!string.Equals(textBefore, Describe(feed) + "\n" + TrackLineOf(feed), StringComparison.Ordinal))
            {
                change |= NowPlayingChange.Description;
            }

            if (!string.Equals(coverBefore, CoverIdOf(feed), StringComparison.Ordinal))
            {
                change |= NowPlayingChange.Cover;
            }

            return change;
        }
    }

    /// <summary>
    /// Forgets the last feed (station unreachable or polling disabled).
    /// </summary>
    /// <returns>True when there was a feed to forget.</returns>
    public bool Clear()
    {
        lock (_gate)
        {
            var had = _feed is not null;
            _feed = null;
            _stationState = null;
            return had;
        }
    }

    /// <summary>
    /// Gets the one-line description of what is on air, or null when nothing is known.
    /// </summary>
    /// <returns>For example "Now playing: Artist – Title (Album) · DJ Bobby · The Ville".</returns>
    public string? Description()
    {
        lock (_gate)
        {
            return Describe(_feed);
        }
    }

    /// <summary>
    /// Stores the last station state (history, upcoming, flags); kept across feed failures until <see cref="Clear"/>.
    /// </summary>
    /// <param name="state">The state just fetched.</param>
    public void UpdateStationState(StationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _stationState = state;
        }
    }

    /// <summary>
    /// Gets the last station state, or null. Callers must not modify it.
    /// </summary>
    /// <returns>The state.</returns>
    public StationState? StationState()
    {
        lock (_gate)
        {
            return _stationState;
        }
    }

    /// <summary>
    /// Gets the last feed as fetched, or null when nothing is known. Callers must not modify it.
    /// </summary>
    /// <returns>The feed.</returns>
    public NowPlayingFeed? Feed()
    {
        lock (_gate)
        {
            return _feed;
        }
    }

    /// <summary>
    /// Gets the short "Artist – Title" line for the playing bar, or null when nothing is on air.
    /// </summary>
    /// <returns>For example "Artist – Title".</returns>
    public string? TrackLine()
    {
        lock (_gate)
        {
            return TrackLineOf(_feed);
        }
    }

    /// <summary>
    /// Gets the station id of the current track's cover art, or null when there is no track, the stream is
    /// reported off air, or the id is not a valid station id.
    /// </summary>
    /// <returns>The cover id for <see cref="StationClient.GetCoverAsync"/>.</returns>
    public string? CoverId()
    {
        lock (_gate)
        {
            return CoverIdOf(_feed);
        }
    }

    private static string? TrackLineOf(NowPlayingFeed? feed)
    {
        if (feed is null || feed.StreamOnline == false)
        {
            return null;
        }

        var artist = Clean(feed.NowPlaying?.Artist);
        var title = Clean(feed.NowPlaying?.Title);
        if (artist.Length == 0 && title.Length == 0)
        {
            return null;
        }

        if (artist.Length == 0)
        {
            return title;
        }

        return title.Length == 0 ? artist : artist + " – " + title;
    }

    private static string? CoverIdOf(NowPlayingFeed? feed)
    {
        if (feed is null || feed.StreamOnline == false)
        {
            return null;
        }

        var id = feed.NowPlaying?.SubsonicId?.Trim();
        return StationClient.IsValidId(id) ? id : null;
    }

    private static string? Describe(NowPlayingFeed? feed)
    {
        if (feed is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        var artist = Clean(feed.NowPlaying?.Artist);
        var title = Clean(feed.NowPlaying?.Title);
        var album = Clean(feed.NowPlaying?.Album);
        var dj = Clean(feed.Dj?.Name);
        var show = Clean(feed.ActiveShow?.Name);

        if (feed.StreamOnline == false)
        {
            // A stale track may still be reported while the mount is down; the listener hears nothing.
            // Only an explicit false counts: a missing flag must never hide the track.
            sb.Append("Off air");
        }
        else if (artist.Length > 0 || title.Length > 0)
        {
            sb.Append("Now playing: ");
            if (artist.Length > 0)
            {
                sb.Append(artist).Append(" – ");
            }

            sb.Append(title.Length > 0 ? title : "untitled");
            if (album.Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({album})");
            }
        }

        if (dj.Length > 0)
        {
            AppendSeparated(sb, "DJ " + dj);
        }

        if (show.Length > 0)
        {
            AppendSeparated(sb, show);
        }

        var next = feed.Context?.ShowHandover?.NextShow;
        var nextName = Clean(next?.Name);
        if (nextName.Length > 0 && !string.Equals(nextName, show, StringComparison.OrdinalIgnoreCase))
        {
            var presenter = Clean(next?.Presenter);
            var startsAt = Clean(next?.StartsAt);
            AppendSeparated(sb, "Up next: " + nextName);
            if (presenter.Length > 0)
            {
                sb.Append(" with ").Append(presenter);
            }

            if (startsAt.Length > 0)
            {
                sb.Append(" at ").Append(startsAt);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Collapses whitespace (including line breaks) to single spaces, trims, and caps the length.
    /// Station text lands in a database column and in client UIs; it must stay one short line.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxFieldLength ? collapsed : collapsed[..(MaxFieldLength - 1)] + "…";
    }

    private static void AppendSeparated(StringBuilder sb, string text)
    {
        if (sb.Length > 0)
        {
            sb.Append(" · ");
        }

        sb.Append(text);
    }
}
