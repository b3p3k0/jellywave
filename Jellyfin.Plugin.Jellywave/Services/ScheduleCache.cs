using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The station's weekly schedule, refetched at most every few minutes: it changes rarely and every open
/// station page would otherwise ask for it. The copy is kept with its station origin (<see cref="StationUrls.Origin"/>):
/// a change of station URL drops it before the new station is asked, so another station's schedule is never served.
/// </summary>
public sealed class ScheduleCache : IDisposable
{
    /// <summary>How long a fetched schedule is served before the station is asked again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a stale copy is served after a failed refresh before the station is asked again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly StationClient _stationClient;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _origin;
    private ScheduleInfo? _schedule;
    private DateTime _fetchedUtc;

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

    /// <summary>
    /// Gets the schedule, from cache when fresh. Station failures propagate; a stale copy of the same station's
    /// schedule is returned instead when there is one.
    /// </summary>
    /// <param name="config">The configuration (for the station URL).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schedule, or null when the station is not configured or answered nothing.</returns>
    public async Task<ScheduleInfo?> GetAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return null;
        }

        var origin = StationUrls.Origin(baseUrl);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(origin, _origin, StringComparison.Ordinal))
            {
                // Another station: its schedule is fetched fresh, and on failure there is no copy to fall back on.
                _schedule = null;
                _origin = origin;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            if (_schedule is not null && now - _fetchedUtc < Lifetime)
            {
                return _schedule;
            }

            try
            {
                var fresh = await _stationClient.GetScheduleAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                if (fresh is not null)
                {
                    _schedule = fresh;
                    _fetchedUtc = _clock.GetUtcNow().UtcDateTime;
                }
            }
            catch (Exception) when (_schedule is not null)
            {
                // Stale beats nothing; try the station again after RetryDelay rather than on every call.
                _fetchedUtc = _clock.GetUtcNow().UtcDateTime - Lifetime + RetryDelay;
            }

            return _schedule;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}
