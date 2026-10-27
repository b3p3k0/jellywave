using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The station's weekly schedule, refetched at most every few minutes: it changes rarely and every open
/// station page would otherwise ask for it.
/// </summary>
public sealed class ScheduleCache : IDisposable
{
    /// <summary>How long a fetched schedule is served before the station is asked again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly StationClient _stationClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScheduleInfo? _schedule;
    private DateTime _fetchedUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleCache"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    public ScheduleCache(StationClient stationClient)
    {
        _stationClient = stationClient;
    }

    /// <summary>
    /// Gets the schedule, from cache when fresh. Station failures propagate; a stale copy is returned instead when there is one.
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schedule is not null && DateTime.UtcNow - _fetchedUtc < Lifetime)
            {
                return _schedule;
            }

            try
            {
                var fresh = await _stationClient.GetScheduleAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                if (fresh is not null)
                {
                    _schedule = fresh;
                    _fetchedUtc = DateTime.UtcNow;
                }
            }
            catch (Exception) when (_schedule is not null)
            {
                // Stale beats nothing; the next call after Lifetime tries again.
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
