using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What the now-playing poller last did, for the configuration page: whether the loop runs, and per polled SUB/WAVE
/// station (card L4) the last success and the last failure's category. Written by the poller, read by the admin endpoint
/// as one snapshot per station, so the page never needs the server log. Keyed like <see cref="NowPlayingState"/>: one
/// entry per station id carrying its origin, so a changed base URL starts afresh, and <see cref="Retain"/> drops stations
/// that are no longer listed. A failure is recorded as a fixed category (<see cref="StationFailures"/>), never as
/// exception text or a URL. Card L14: a station the poller leaves idle (nobody needs it) is marked so, not as a failure;
/// "Reload status" records what it found (<see cref="Checked"/>) without ending the idle mark.
/// </summary>
public sealed class PollerHealth
{
    /// <summary>Decimals kept of the fetch time.</summary>
    private const int FetchSecondsDigits = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _running;
    private double? _lastFetchSeconds;

    /// <summary>
    /// Reads whether the loop runs and one station's fields under one lock.
    /// </summary>
    /// <param name="key">The station, or null for the loop alone.</param>
    /// <returns>The snapshot; the station fields are empty when the station has not been polled under this key.</returns>
    public PollerHealthSnapshot Snapshot(StationKey? key)
    {
        lock (_gate)
        {
            var snapshot = new PollerHealthSnapshot { Running = _running, LastFetchSeconds = _lastFetchSeconds };
            if (key is not null && _entries.TryGetValue(key.StationId, out var entry) && string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal))
            {
                snapshot.Reachable = entry.Reachable;
                snapshot.LastSuccessUtc = entry.LastSuccessUtc;
                snapshot.LastErrorUtc = entry.LastErrorUtc;
                snapshot.LastError = entry.LastError;
                snapshot.IdleSinceUtc = entry.IdleSinceUtc;
            }

            return snapshot;
        }
    }

    /// <summary>Records that the loop started or stopped.</summary>
    /// <param name="running">Whether it is running.</param>
    public void SetRunning(bool running)
    {
        lock (_gate)
        {
            _running = running;
        }
    }

    /// <summary>Records how long the last regular poll's fetch phase took (every needed SUB/WAVE station, at most four at once).</summary>
    /// <param name="elapsed">The duration.</param>
    public void FetchTook(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _lastFetchSeconds = Math.Round(elapsed.TotalSeconds, FetchSecondsDigits);
        }
    }

    /// <summary>
    /// Records that the last regular poll asked no station (card L14: nobody needs one), so the page shows no fetch time
    /// from a poll long ago.
    /// </summary>
    public void NothingAsked()
    {
        lock (_gate)
        {
            _lastFetchSeconds = null;
        }
    }

    /// <summary>Records a successful poll of one station; the station is no longer idle.</summary>
    /// <param name="key">The station.</param>
    /// <param name="nowUtc">The time.</param>
    public void Success(StationKey key, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = EntryFor(key);
            entry.IdleSinceUtc = null;
            Answered(entry, nowUtc, null);
        }
    }

    /// <summary>Records a failed poll of one station; the station is no longer idle.</summary>
    /// <param name="key">The station.</param>
    /// <param name="nowUtc">The time.</param>
    /// <param name="category">What went wrong: a fixed category from <see cref="StationFailures"/>.</param>
    public void Failure(StationKey key, DateTime nowUtc, string category)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = EntryFor(key);
            entry.IdleSinceUtc = null;
            Answered(entry, nowUtc, category);
        }
    }

    /// <summary>
    /// Records that the poller left one station idle (card L14: nobody plays it or has its page open, so it was not asked).
    /// Not a failure: the last success and failure stay as they were, and the idle time is the first poll that left it idle.
    /// </summary>
    /// <param name="key">The station.</param>
    /// <param name="nowUtc">The time.</param>
    public void Idle(StationKey key, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = EntryFor(key);
            entry.IdleSinceUtc ??= nowUtc;
        }
    }

    /// <summary>
    /// Records what "Reload status" found when it asked one station on purpose (card L14): a success, or a failure with its
    /// category. An idle station stays idle; the page shows the check beside the idle mark.
    /// </summary>
    /// <param name="key">The station.</param>
    /// <param name="nowUtc">The time.</param>
    /// <param name="category">Null for a success, else what went wrong: a fixed category from <see cref="StationFailures"/>.</param>
    public void Checked(StationKey key, DateTime nowUtc, string? category)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            Answered(EntryFor(key), nowUtc, category);
        }
    }

    /// <summary>
    /// Drops the entries of every station not in <paramref name="stationIds"/>.
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

    // Under _gate.
    private static void Answered(Entry entry, DateTime nowUtc, string? category)
    {
        entry.Reachable = category is null;
        if (category is null)
        {
            entry.LastSuccessUtc = nowUtc;
        }
        else
        {
            entry.LastErrorUtc = nowUtc;
            entry.LastError = category;
        }
    }

    // Under _gate. Another origin under the same id is another station: its history starts afresh.
    private Entry EntryFor(StationKey key)
    {
        if (!_entries.TryGetValue(key.StationId, out var entry) || !string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal))
        {
            entry = new Entry(key.Origin);
            _entries[key.StationId] = entry;
        }

        return entry;
    }

    /// <summary>One station's health, under one origin.</summary>
    private sealed class Entry
    {
        public Entry(string origin) => Origin = origin;

        public string Origin { get; }

        public bool? Reachable { get; set; }

        public DateTime? LastSuccessUtc { get; set; }

        public DateTime? LastErrorUtc { get; set; }

        public string? LastError { get; set; }

        public DateTime? IdleSinceUtc { get; set; }
    }
}
