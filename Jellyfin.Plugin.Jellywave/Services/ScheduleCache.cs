using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Each SUB/WAVE station's weekly schedule, refetched at most every few minutes: it changes rarely and every open station
/// page would otherwise ask for it. One copy per station id (card L4), kept with its station origin
/// (<see cref="StationKey"/>): a change of the station's base URL drops its copy before the new origin is asked, so
/// another station's schedule is never served. The poller drops the copies of stations no longer listed
/// (<see cref="Retain"/>), so the map holds at most one copy per listed station. Fetches are serialized per station
/// (card L5: every station's page asks), so a slow or unreachable station never holds up another station's schedule.
/// </summary>
public sealed class ScheduleCache : IDisposable
{
    /// <summary>How long a fetched schedule is served before the station is asked again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a stale copy is served after a failed refresh before the station is asked again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly StationClient _stationClient;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleCache"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    public ScheduleCache(StationClient stationClient)
        : this(stationClient, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleCache"/> class with an explicit clock (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="clock">The clock.</param>
    internal ScheduleCache(StationClient stationClient, TimeProvider clock)
    {
        _stationClient = stationClient;
        _clock = clock;
    }

    /// <summary>Gets the number of per-station fetch gates held (tests: <see cref="Retain"/> drops unlisted stations' gates).</summary>
    internal int GateCount
    {
        get
        {
            lock (_gate)
            {
                return _fetchGates.Count;
            }
        }
    }

    /// <summary>
    /// Gets a station's schedule, from cache when fresh. Station failures propagate; a stale copy of the same station's
    /// schedule is returned instead when there is one. Without a copy, a failure is not retried for
    /// <see cref="RetryDelay"/>: calls in that time return null without asking the station.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="baseUrl">The station's parsed base URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schedule, or null when the station answered nothing.</returns>
    public async Task<ScheduleInfo?> GetAsync(string stationId, Uri baseUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(baseUrl);
        var origin = StationUrls.Origin(baseUrl);
        var fetchGate = FetchGateOf(stationId);
        await fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(stationId, out entry!) || !string.Equals(entry.Origin, origin, StringComparison.Ordinal))
                {
                    // Another origin: its schedule is fetched fresh, and on failure there is no copy to fall back on.
                    entry = new Entry(origin);
                    _entries[stationId] = entry;
                }
            }

            // The entry is read and written only under its station's fetch gate; Retain may drop it meanwhile, and then
            // this answer is simply not kept.
            var now = _clock.GetUtcNow().UtcDateTime;
            if (entry.Schedule is not null && now - entry.FetchedUtc < Lifetime)
            {
                return entry.Schedule;
            }

            if (entry.Schedule is null && now < entry.NoCopyRetryUtc)
            {
                return null;
            }

            try
            {
                var fresh = await _stationClient.GetScheduleAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                if (fresh is not null)
                {
                    entry.Schedule = fresh;
                    entry.FetchedUtc = _clock.GetUtcNow().UtcDateTime;
                }
            }
            catch (Exception) when (entry.Schedule is not null)
            {
                // Stale beats nothing; try the station again after RetryDelay rather than on every call.
                entry.FetchedUtc = _clock.GetUtcNow().UtcDateTime - Lifetime + RetryDelay;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No copy to serve: this caller sees the failure, and the next RetryDelay of calls do not ask again.
                entry.NoCopyRetryUtc = _clock.GetUtcNow().UtcDateTime + RetryDelay;
                throw;
            }

            return entry.Schedule;
        }
        finally
        {
            fetchGate.Release();
        }
    }

    /// <summary>
    /// Drops the copies of every station not in <paramref name="stationIds"/> (removed, disabled or no longer listed).
    /// </summary>
    /// <param name="stationIds">The ids of the listed SUB/WAVE stations.</param>
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

            // A dropped gate may still be held by a fetch in flight, so it is not disposed here (a SemaphoreSlim whose
            // wait handle was never asked for holds no unmanaged resource). Requests reach this cache only for listed
            // stations, so the map stays bounded by the listed ones.
            foreach (var id in new List<string>(_fetchGates.Keys))
            {
                if (!stationIds.Contains(id))
                {
                    _fetchGates.Remove(id);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var gate in _fetchGates.Values)
            {
                gate.Dispose();
            }

            _fetchGates.Clear();
        }
    }

    /// <summary>Gets the station's fetch gate, made on first use.</summary>
    private SemaphoreSlim FetchGateOf(string stationId)
    {
        lock (_gate)
        {
            if (!_fetchGates.TryGetValue(stationId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _fetchGates[stationId] = gate;
            }

            return gate;
        }
    }

    /// <summary>One station's copy, under one origin.</summary>
    private sealed class Entry
    {
        public Entry(string origin) => Origin = origin;

        public string Origin { get; }

        public ScheduleInfo? Schedule { get; set; }

        public DateTime FetchedUtc { get; set; }

        public DateTime NoCopyRetryUtc { get; set; }
    }
}
