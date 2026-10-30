using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The latest now-playing data of every polled SUB/WAVE station, shared between the poller, the channel and the station
/// routes. Thread-safe. One entry per station id (card L4); each entry carries its station origin, so a feed under a key
/// with another origin (the base URL changed under the same id) replaces the entry, and a reader asking with the new key
/// gets <see cref="NowPlayingSnapshot.OtherStation"/> until the poller has heard the new origin (card 17). The poller
/// drops the entries of stations that are no longer listed (<see cref="Retain"/>), so the map holds at most one entry per
/// listed station.
/// </summary>
public sealed class NowPlayingState
{
    /// <summary>
    /// Longest station-supplied field kept in the description (the rest is cut with an ellipsis).
    /// </summary>
    public const int MaxFieldLength = 120;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Stores a fresh feed and reports what the station's item would show differently. A feed under another origin than
    /// the station's last one starts a new entry, so the new origin's feed never sits beside the old one's history.
    /// </summary>
    /// <param name="key">The station the feed came from.</param>
    /// <param name="feed">The feed just fetched.</param>
    /// <param name="nowUtc">The fetch time.</param>
    /// <returns>Which of the description, track line and cover id differ from the station's previous feed.</returns>
    public NowPlayingChange Update(StationKey key, NowPlayingFeed feed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(feed);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key.StationId, out var entry) || !string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal))
            {
                entry = new Entry(key.Origin);
                _entries[key.StationId] = entry;
            }

            var textBefore = Describe(entry.Feed) + "\n" + TrackLineOf(entry.Feed);
            var coverBefore = CoverIdOf(entry.Feed);
            entry.Feed = feed;
            entry.FetchedUtc = nowUtc;

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
    /// Forgets one station's data (the station unreachable or answering with an empty feed).
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <returns>True when there was data to forget.</returns>
    public bool Clear(string stationId)
    {
        lock (_gate)
        {
            return _entries.Remove(stationId);
        }
    }

    /// <summary>
    /// Forgets every station's data (polling switched off).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Drops the data of every station not in <paramref name="stationIds"/> (removed, disabled or no longer listed).
    /// </summary>
    /// <param name="stationIds">The ids of the stations the poller polls now.</param>
    public void Retain(IReadOnlySet<string> stationIds)
    {
        ArgumentNullException.ThrowIfNull(stationIds);
        lock (_gate)
        {
            foreach (var id in new List<string>(_entries.Keys))
            {
                if (!stationIds.Contains(id))
                {
                    _entries.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Stores a station's last station state (history, upcoming, flags). Kept across failed state fetches from the same
    /// station; dropped by <see cref="Clear(string)"/> and by a feed from another origin. A state under another key than
    /// the station's current feed is not stored.
    /// </summary>
    /// <param name="key">The station the state came from.</param>
    /// <param name="state">The state just fetched.</param>
    /// <returns>True when it was stored.</returns>
    public bool UpdateStationState(StationKey key, StationState state)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key.StationId, out var entry) || !string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal))
            {
                return false;
            }

            entry.StationState = state;
            return true;
        }
    }

    /// <summary>
    /// Reads one station's feed, station state and the values derived from the feed under one lock, so a caller never
    /// pairs one station's feed with another's history. An entry under another origin than the key's is not served.
    /// </summary>
    /// <param name="key">The station.</param>
    /// <returns>The snapshot; <see cref="NowPlayingSnapshot.Nothing"/> when nothing is known of the station, and
    /// <see cref="NowPlayingSnapshot.OtherStation"/> when its entry is another origin's.</returns>
    public NowPlayingSnapshot Snapshot(StationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key.StationId, out var entry))
            {
                return NowPlayingSnapshot.Nothing;
            }

            if (!string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal))
            {
                return NowPlayingSnapshot.OtherStation;
            }

            var feed = entry.Feed;
            return new NowPlayingSnapshot(feed, entry.StationState, entry.FetchedUtc, CoverIdOf(feed), TrackLineOf(feed), Describe(feed), false);
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

    /// <summary>What is known of one station, under one origin.</summary>
    private sealed class Entry
    {
        public Entry(string origin) => Origin = origin;

        public string Origin { get; }

        public NowPlayingFeed? Feed { get; set; }

        public StationState? StationState { get; set; }

        public DateTime FetchedUtc { get; set; }
    }
}
