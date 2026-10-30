using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Card R2a: one limiter for every user-driven station call (like status and like, lyrics, portrait, request and request
/// status): at most <see cref="MaxPerStation"/> calls in flight to one station and <see cref="MaxTotal"/> in all, and of
/// those at most <see cref="MaxPortraits"/> portrait fetches (each may buffer up to 4 MB, so they are the memory peak).
/// Nothing waits: a call over any limit is refused at once (the route answers 503 with <c>Retry-After</c>), so there is no
/// queue to grow. The station slot is checked first and the total (and portrait) slot taken only with it, under one lock
/// and without waiting, so callers stuck on one slow station hold at most <see cref="MaxPerStation"/> of the total and
/// never block another station's calls. A station is counted by <see cref="StationKey.Origin"/>, which is its whole
/// parsed base URL: two station ids with the same base URL share its slots; two base URLs on one host do not. The
/// poller, the title sampler, the schedule and "Reload status" do not take permits here.
/// </summary>
public sealed class StationCallLimiter
{
    /// <summary>The most user-driven calls in flight to one station.</summary>
    public const int MaxPerStation = 4;

    /// <summary>The most user-driven station calls in flight in all.</summary>
    public const int MaxTotal = 16;

    /// <summary>The most portrait fetches in flight in all (counted within <see cref="MaxTotal"/>).</summary>
    public const int MaxPortraits = 4;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _perStation = new(StringComparer.Ordinal);
    private readonly int _maxPerStation;
    private readonly int _maxTotal;
    private readonly int _maxPortraits;
    private int _total;
    private int _portraits;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCallLimiter"/> class with the card's limits.
    /// </summary>
    public StationCallLimiter()
        : this(MaxPerStation, MaxTotal, MaxPortraits)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCallLimiter"/> class with other limits (tests).
    /// </summary>
    /// <param name="maxPerStation">The most calls in flight to one station.</param>
    /// <param name="maxTotal">The most calls in flight in all.</param>
    /// <param name="maxPortraits">The most portrait fetches in flight in all.</param>
    internal StationCallLimiter(int maxPerStation, int maxTotal, int maxPortraits)
    {
        _maxPerStation = maxPerStation;
        _maxTotal = maxTotal;
        _maxPortraits = maxPortraits;
    }

    /// <summary>Gets the number of portrait permits held (tests).</summary>
    internal int PortraitsInUse
    {
        get
        {
            lock (_gate)
            {
                return _portraits;
            }
        }
    }

    /// <summary>Gets the number of permits held in all (tests).</summary>
    internal int InUse
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>Gets the number of stations with a permit held (tests: the map holds only those).</summary>
    internal int StationsInUse
    {
        get
        {
            lock (_gate)
            {
                return _perStation.Count;
            }
        }
    }

    /// <summary>
    /// Takes a permit for one call to a station, or refuses at once.
    /// </summary>
    /// <param name="station">The station's origin.</param>
    /// <param name="portrait">Whether the call fetches a portrait (also counted against <see cref="MaxPortraits"/>).</param>
    /// <returns>The permit, to dispose when the answer is read and disposed; null when the station, the total or (for a
    /// portrait) the portrait slots are full.</returns>
    public StationCallPermit? TryEnter(string station, bool portrait = false)
    {
        ArgumentNullException.ThrowIfNull(station);
        lock (_gate)
        {
            var held = _perStation.GetValueOrDefault(station);
            if (held >= _maxPerStation || _total >= _maxTotal || (portrait && _portraits >= _maxPortraits))
            {
                return null;
            }

            _perStation[station] = held + 1;
            _total++;
            if (portrait)
            {
                _portraits++;
            }

            return new StationCallPermit(this, station, portrait);
        }
    }

    /// <summary>Gives a permit's slots back (<see cref="StationCallPermit.Dispose"/>).</summary>
    /// <param name="station">The station's origin.</param>
    /// <param name="portrait">Whether it held a portrait slot.</param>
    internal void Release(string station, bool portrait)
    {
        lock (_gate)
        {
            var held = _perStation.GetValueOrDefault(station) - 1;
            if (held <= 0)
            {
                _perStation.Remove(station);
            }
            else
            {
                _perStation[station] = held;
            }

            _total--;
            if (portrait)
            {
                _portraits--;
            }
        }
    }
}
