using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Card R2a: a bounded cache of station answers for the station routes, used only by <see cref="StationCalls"/>, which
/// the routes call after their access gate and station resolution, with the filter's station key. Bounded by entry
/// count (in all and per station) and by total bytes (each answer's estimated size); the entry that expires soonest goes
/// first (the oldest among equals), so a short-lived error entry is evicted before a long-lived answer, and an answer
/// larger than the whole budget is not kept. Each answer's lifetime is chosen by the caller (errors shorter).
/// Concurrent misses for one key share one fetch: the first caller is admitted (budget and permit) and starts it, the
/// others wait for its answer without a budget or permit of their own; a waiter that gives up stops waiting, and when
/// every waiter has given up the fetch is cancelled. A refusal at admission goes to that caller only and is never kept.
/// </summary>
/// <typeparam name="T">The answer type.</typeparam>
internal sealed class StationCache<T>
    where T : class
{
    private readonly TimeProvider _clock;
    private readonly int _maxEntries;
    private readonly int _maxPerStation;
    private readonly long _maxBytes;
    private readonly Func<StationCall<T>, long> _sizeOf;
    private readonly object _gate = new();
    private readonly Dictionary<StationCacheKey, Entry> _entries = new();
    private readonly Dictionary<StationCacheKey, Flight> _flights = new();
    private long _bytes;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCache{T}"/> class.
    /// </summary>
    /// <param name="clock">The clock.</param>
    /// <param name="maxEntries">The most answers kept in all.</param>
    /// <param name="maxPerStation">The most answers kept for one station key.</param>
    /// <param name="maxBytes">The most bytes kept in all, by <paramref name="sizeOf"/>.</param>
    /// <param name="sizeOf">An answer's estimated size in bytes.</param>
    internal StationCache(TimeProvider clock, int maxEntries, int maxPerStation, long maxBytes, Func<StationCall<T>, long> sizeOf)
    {
        _clock = clock;
        _maxEntries = maxEntries;
        _maxPerStation = maxPerStation;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
    }

    /// <summary>Gets the number of answers kept (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Gets the estimated bytes kept (tests).</summary>
    internal long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// Gets an answer: a live cached one, the one a fetch in flight for the same key brings, or a new fetch's.
    /// </summary>
    /// <param name="key">The station key and the item.</param>
    /// <param name="admit">Called under the cache's lock when this caller must start the fetch; must not block. Returns a
    /// refusal for this caller, or the fetch (already admitted) to run with the shared fetch's cancellation token.</param>
    /// <param name="lifetimeOf">How long an answer is kept; zero or less keeps it not at all.</param>
    /// <param name="cancellationToken">This caller's cancellation token: stops this caller waiting.</param>
    /// <returns>The answer.</returns>
    internal async Task<StationCall<T>> GetAsync(
        StationCacheKey key,
        Func<(StationCall<T>? Refusal, Func<CancellationToken, Task<StationCall<T>>>? Fetch)> admit,
        Func<StationCall<T>, TimeSpan> lifetimeOf,
        CancellationToken cancellationToken)
    {
        Flight flight;
        Func<CancellationToken, Task<StationCall<T>>>? fetch = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Expires > _clock.GetUtcNow())
                {
                    return entry.Value;
                }

                Remove(key, entry);
            }

            if (_flights.TryGetValue(key, out var shared))
            {
                flight = shared;
                flight.Waiters++;
            }
            else
            {
                var (refusal, admitted) = admit();
                if (refusal is not null || admitted is null)
                {
                    return refusal ?? throw new InvalidOperationException("Neither a refusal nor a fetch.");
                }

                fetch = admitted;
                flight = new Flight();
                _flights[key] = flight;
            }
        }

        if (fetch is not null)
        {
            _ = RunAsync(key, flight, fetch, lifetimeOf);
        }

        try
        {
            return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Leave(key, flight);
            throw;
        }
    }

    /// <summary>
    /// Drops the answer for a key, and lets no fetch in flight for it keep its answer (it may predate the change).
    /// </summary>
    /// <param name="key">The key.</param>
    internal void Invalidate(StationCacheKey key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                Remove(key, entry);
            }

            if (_flights.Remove(key, out var flight))
            {
                flight.Stale = true;
            }
        }
    }

    /// <summary>Drops every answer of the key's station for another item (like status: the song changed).</summary>
    /// <param name="keep">The station and the item to keep.</param>
    internal void DropOthers(StationCacheKey keep)
    {
        lock (_gate)
        {
            foreach (var (key, entry) in _entries.Where(e => e.Key.Station == keep.Station && e.Key.Item != keep.Item).ToList())
            {
                Remove(key, entry);
            }
        }
    }

    private async Task RunAsync(StationCacheKey key, Flight flight, Func<CancellationToken, Task<StationCall<T>>> fetch, Func<StationCall<T>, TimeSpan> lifetimeOf)
    {
        StationCall<T> answer;
        try
        {
            answer = await fetch(flight.Cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Finish(key, flight);
            if (ex is OperationCanceledException)
            {
                flight.Completion.TrySetCanceled();
            }
            else
            {
                flight.Completion.TrySetException(ex);
            }

            return;
        }

        if (Finish(key, flight))
        {
            lock (_gate)
            {
                Store(key, answer, lifetimeOf(answer));
            }
        }

        flight.Completion.TrySetResult(answer);
    }

    /// <summary>Ends a flight; true when it was still the key's own (nobody left it, nothing invalidated it).</summary>
    private bool Finish(StationCacheKey key, Flight flight)
    {
        bool current;
        lock (_gate)
        {
            current = _flights.TryGetValue(key, out var f) && f == flight && !flight.Stale;
            if (_flights.TryGetValue(key, out f) && f == flight)
            {
                _flights.Remove(key);
            }

            flight.Done = true;
        }

        flight.Cancel.Dispose();
        return current;
    }

    /// <summary>One waiter gave up; the last one cancels the fetch.</summary>
    private void Leave(StationCacheKey key, Flight flight)
    {
        bool cancel;
        lock (_gate)
        {
            cancel = --flight.Waiters == 0 && !flight.Done;
            if (cancel && _flights.TryGetValue(key, out var f) && f == flight)
            {
                _flights.Remove(key);
            }
        }

        if (cancel)
        {
            try
            {
                flight.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished in between.
            }
        }
    }

    private void Store(StationCacheKey key, StationCall<T> answer, TimeSpan lifetime)
    {
        var size = _sizeOf(answer);
        if (lifetime <= TimeSpan.Zero || size > _maxBytes)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (_entries.TryGetValue(key, out var old))
        {
            Remove(key, old);
        }

        foreach (var (k, e) in _entries.Where(e => e.Value.Expires <= now).ToList())
        {
            Remove(k, e);
        }

        while (_entries.Count(e => e.Key.Station == key.Station) >= _maxPerStation)
        {
            var first = _entries.Where(e => e.Key.Station == key.Station).OrderBy(e => e.Value.Expires).ThenBy(e => e.Value.Sequence).First();
            Remove(first.Key, first.Value);
        }

        while (_entries.Count >= _maxEntries || _bytes + size > _maxBytes)
        {
            var first = _entries.OrderBy(e => e.Value.Expires).ThenBy(e => e.Value.Sequence).First();
            Remove(first.Key, first.Value);
        }

        _entries[key] = new Entry(answer, now + lifetime, size, ++_sequence);
        _bytes += size;
    }

    private void Remove(StationCacheKey key, Entry entry)
    {
        _entries.Remove(key);
        _bytes -= entry.Size;
    }

    private sealed record Entry(StationCall<T> Value, DateTimeOffset Expires, long Size, long Sequence);

    /// <summary>A fetch in flight and the callers waiting for it.</summary>
    private sealed class Flight
    {
        public TaskCompletionSource<StationCall<T>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancel { get; } = new();

        public int Waiters { get; set; } = 1;

        public bool Done { get; set; }

        public bool Stale { get; set; }
    }
}
