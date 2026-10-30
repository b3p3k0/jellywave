using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What <see cref="TitleSampler"/> last learned of each plain stream someone plays (card L6), keyed by station id: the
/// state, the cleaned title and the time of the last good sample. Written by the sampler only; read by the title route,
/// which never samples. An entry lives while someone plays its station: <see cref="Expire"/> drops one nobody has played
/// for a few sample intervals, and one whose station is no longer a listed plain stream. Thread-safe.
/// Card L13: it also keeps, per station, the outcome and time of the last read (<see cref="LastRead"/>) for the
/// configuration page's health, in memory only and since the last restart. That record outlives the title entry (it
/// says when the stream last answered, not what it plays), and is tied to the stream URL it was read from by a hash, so
/// a station whose URL changed shows no outcome from its old URL; the URL itself is never kept.
/// </summary>
public sealed class StationTitles
{
    /// <summary>State: no sample yet (nobody played the station recently, or the first sample is still to come).</summary>
    public const string StateWaiting = "Waiting";

    /// <summary>State: the last sample read a title.</summary>
    public const string StateTitle = "Title";

    /// <summary>
    /// State: the stream sends no titles: no usable <c>icy-metaint</c> (or one over the cap), an HLS stream, or a metadata
    /// block without a title.
    /// </summary>
    public const string StateNoTitles = "NoTitles";

    /// <summary>State: the last sample failed (not reachable, a redirect, an error status, a timeout, the stream broke off).</summary>
    public const string StateFailing = "Failing";

    /// <summary>
    /// State: "Show what's playing" (<c>NowPlayingEnabled</c>) is off (HI 2026-10-28), so nothing is sampled; every
    /// station reads this until the sampler sees it on again.
    /// </summary>
    public const string StateOff = "Off";

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadEntry> _reads = new(StringComparer.Ordinal);
    private bool _enabled = true;

    /// <summary>
    /// Gets a station's title state: a copy, <see cref="StateWaiting"/> when there is none.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <returns>The snapshot.</returns>
    public TitleSnapshot Snapshot(string stationId)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return new TitleSnapshot { State = StateOff };
            }

            if (stationId is null || !_entries.TryGetValue(stationId, out var entry))
            {
                return new TitleSnapshot();
            }

            return new TitleSnapshot
            {
                State = entry.State,
                Title = entry.State == StateTitle ? entry.Title : null,
                LastGoodUtc = entry.LastGoodUtc
            };
        }
    }

    /// <summary>
    /// Gets the outcome of the last read of a station's stream (card L13), or null when the sampler has not read this
    /// stream URL since the last restart (nobody played it with "Show what's playing" on, an HLS stream, which is never
    /// read, or a URL changed since the read). Survives "Show what's playing" being switched off: it is history, not a
    /// title.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="streamUrl">The station's stream URL now; a read of another URL does not count.</param>
    /// <returns>A copy, or null.</returns>
    public TitleRead? LastRead(string stationId, Uri streamUrl)
    {
        ArgumentNullException.ThrowIfNull(streamUrl);
        lock (_gate)
        {
            if (stationId is null || !_reads.TryGetValue(stationId, out var read) || read.Source != SourceOf(streamUrl))
            {
                return null;
            }

            return new TitleRead { Outcome = read.Outcome, AtUtc = read.AtUtc };
        }
    }

    /// <summary>
    /// Records whether title sampling is on (the sampler, every round, from <c>NowPlayingEnabled</c>). Off drops every
    /// entry, so a title is never shown from before the switch.
    /// </summary>
    /// <param name="enabled">Whether sampling is on.</param>
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _entries.Clear();
            }
        }
    }

    /// <summary>
    /// Records that someone plays the station now, so its entry is kept (one is made, waiting, when there is none).
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="nowUtc">The time.</param>
    internal void Played(string stationId, DateTime nowUtc)
    {
        lock (_gate)
        {
            EntryOf(stationId, nowUtc).LastPlayedUtc = nowUtc;
        }
    }

    /// <summary>
    /// Records a sample's outcome.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="state">One of the <c>State*</c> constants.</param>
    /// <param name="title">The cleaned title for <see cref="StateTitle"/>; ignored otherwise.</param>
    /// <param name="nowUtc">The time.</param>
    internal void Record(string stationId, string state, string? title, DateTime nowUtc)
    {
        lock (_gate)
        {
            var entry = EntryOf(stationId, nowUtc);
            entry.State = state;
            entry.Title = state == StateTitle ? title : null;
            if (state is StateTitle or StateNoTitles)
            {
                entry.LastGoodUtc = nowUtc;
            }
        }
    }

    /// <summary>
    /// Records the outcome of a read of the station's stream (card L13): the sampler calls it for every request it made,
    /// never for an HLS stream.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="streamUrl">The stream URL read; only its hash is kept.</param>
    /// <param name="outcome">How the read ended.</param>
    /// <param name="nowUtc">The time.</param>
    internal void RecordRead(string stationId, Uri streamUrl, TitleReadOutcome outcome, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(streamUrl);
        lock (_gate)
        {
            _reads[stationId] = new ReadEntry(SourceOf(streamUrl), outcome, nowUtc);
        }
    }

    /// <summary>
    /// Drops the read outcomes of stations that are no longer listed plain streams, or whose stream URL changed (card L13).
    /// </summary>
    /// <param name="listed">The listed plain streams' ids and stream URLs.</param>
    internal void KeepReads(IReadOnlyDictionary<string, Uri> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        lock (_gate)
        {
            foreach (var (id, read) in new List<KeyValuePair<string, ReadEntry>>(_reads))
            {
                if (!listed.TryGetValue(id, out var url) || read.Source != SourceOf(url))
                {
                    _reads.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Gets how many stations have a read outcome (tests).
    /// </summary>
    /// <returns>The count.</returns>
    internal int ReadCount()
    {
        lock (_gate)
        {
            return _reads.Count;
        }
    }

    /// <summary>
    /// Drops the entries of stations that are no longer listed plain streams, and of stations nobody has played since
    /// <paramref name="playedSinceUtc"/>.
    /// </summary>
    /// <param name="listed">The ids of the listed plain streams.</param>
    /// <param name="playedSinceUtc">The oldest last-played time kept.</param>
    internal void Expire(IReadOnlySet<string> listed, DateTime playedSinceUtc)
    {
        ArgumentNullException.ThrowIfNull(listed);
        lock (_gate)
        {
            foreach (var (id, entry) in new List<KeyValuePair<string, Entry>>(_entries))
            {
                if (!listed.Contains(id) || entry.LastPlayedUtc < playedSinceUtc)
                {
                    _entries.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Gets how many stations have an entry (tests).
    /// </summary>
    /// <returns>The count.</returns>
    internal int Count()
    {
        lock (_gate)
        {
            return _entries.Count;
        }
    }

    /// <summary>
    /// The stream URL's hash, so a read is tied to the URL it was made from without keeping the URL. Process-local
    /// (string hashes are randomized per process), like the record itself.
    /// </summary>
    private static int SourceOf(Uri streamUrl) => streamUrl.AbsoluteUri.GetHashCode(StringComparison.Ordinal);

    private Entry EntryOf(string stationId, DateTime nowUtc)
    {
        if (!_entries.TryGetValue(stationId, out var entry))
        {
            entry = new Entry { LastPlayedUtc = nowUtc };
            _entries[stationId] = entry;
        }

        return entry;
    }

    private sealed class Entry
    {
        public string State { get; set; } = StateWaiting;

        public string? Title { get; set; }

        public DateTime? LastGoodUtc { get; set; }

        public DateTime LastPlayedUtc { get; set; }
    }

    private sealed record ReadEntry(int Source, TitleReadOutcome Outcome, DateTime AtUtc);
}
