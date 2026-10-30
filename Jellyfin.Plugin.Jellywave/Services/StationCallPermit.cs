using System;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// One permit from <see cref="StationCallLimiter"/>: a station slot, a slot of the total and, for a portrait, a portrait slot. Held until the station's
/// answer is read in full and disposed; disposing it gives both back, once, whatever ended the call (an answer, a
/// cancellation, any exception).
/// </summary>
public sealed class StationCallPermit : IDisposable
{
    private readonly StationCallLimiter _limiter;
    private readonly string _station;
    private readonly bool _portrait;
    private int _released;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCallPermit"/> class. Only the limiter makes one.
    /// </summary>
    /// <param name="limiter">The limiter the slots belong to.</param>
    /// <param name="station">The station the slot is for (its origin).</param>
    /// <param name="portrait">Whether it holds a portrait slot too.</param>
    internal StationCallPermit(StationCallLimiter limiter, string station, bool portrait)
    {
        _limiter = limiter;
        _station = station;
        _portrait = portrait;
    }

    /// <summary>Gives the slots back; a second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.Release(_station, _portrait);
        }
    }
}
