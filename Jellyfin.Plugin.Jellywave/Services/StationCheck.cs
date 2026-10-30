using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The configuration page's "Reload status" (card L14): asks every listed SUB/WAVE station for its feed once, on purpose,
/// because the poller asks only the stations someone needs (<see cref="StationDemand"/>), and records each answer in
/// <see cref="PollerHealth"/> (<see cref="PollerHealth.Checked"/>), so the status column shows a real result for an idle
/// station too. Touches no state, no item and no warning streak; an idle station stays idle. At most
/// <see cref="NowPlayingService.MaxConcurrentStations"/> stations at a time, one check at a time, and none within
/// <see cref="Cooldown"/> of the last one's end: a press then asks nothing, and the page shows the health as it stands (the
/// last check's results, or the running check's so far).
/// </summary>
public sealed class StationCheck
{
    /// <summary>
    /// Shortest time between the end of one check and the start of the next (fix round S3), so pressing Reload status over
    /// and over cannot ask the stations faster than this.
    /// </summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private readonly StationClient _client;
    private readonly PollerHealth _health;
    private readonly ILogger<StationCheck> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private bool _running;
    private long? _endedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCheck"/> class.
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="health">The poll health the answers are recorded in.</param>
    /// <param name="logger">The logger.</param>
    public StationCheck(StationClient client, PollerHealth health, ILogger<StationCheck> logger)
        : this(client, health, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCheck"/> class with an explicit clock (tests).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="health">The poll health the answers are recorded in.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock; only its timestamps are read.</param>
    internal StationCheck(StationClient client, PollerHealth health, ILogger<StationCheck> logger, TimeProvider clock)
    {
        _client = client;
        _health = health;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Asks every listed SUB/WAVE station once. Nothing is asked with "Show what's playing" off (the stations are then
    /// never asked), without a configuration, while another check runs, or within <see cref="Cooldown"/> of the last one.
    /// </summary>
    /// <param name="config">The configuration, or null when the plugin is not ready.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many stations were asked.</returns>
    public async Task<int> CheckAsync(PluginConfiguration? config, CancellationToken cancellationToken)
    {
        if (config is null || !config.NowPlayingEnabled || !TryStart())
        {
            return 0;
        }

        try
        {
            var stations = StationSources.Publish(config).Where(s => s.Key is not null).ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = NowPlayingService.MaxConcurrentStations, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(stations, options, async (source, token) => await CheckOneAsync(source, token).ConfigureAwait(false)).ConfigureAwait(false);
            return stations.Count;
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _endedAt = _clock.GetTimestamp();
            }
        }
    }

    private bool TryStart()
    {
        lock (_gate)
        {
            if (_running || (_endedAt is { } ended && _clock.GetElapsedTime(ended) < Cooldown))
            {
                return false;
            }

            _running = true;
            return true;
        }
    }

    /// <summary>One station's feed, recorded as a success or a fixed failure category. Throws only on cancellation.</summary>
    private async Task CheckOneAsync(StationSource source, CancellationToken cancellationToken)
    {
        var key = source.Key!;
        string? category;
        try
        {
            var feed = await _client.GetNowPlayingAsync(source.BaseUrl!, cancellationToken).ConfigureAwait(false);
            category = feed is null ? StationFailures.EmptyFeed : null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && StationFailures.IsStationFailure(ex))
        {
            category = StationFailures.Category(ex);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Checking station {Name} failed unexpectedly", JellywaveChannel.StationName(source.Station));
            category = StationFailures.Unexpected;
        }

        _health.Checked(key, DateTime.UtcNow, category);
        _logger.LogDebug("Station {Name} checked: {Result}", JellywaveChannel.StationName(source.Station), category ?? "answered");
    }
}
