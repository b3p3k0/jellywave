using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Polls the station's now-playing feed and writes a one-line description onto the live item.
/// Only <see cref="BaseItem.Overview"/> is written: setting Artists on an Audio item would make
/// Jellyfin create a Music-library artist entry for every track the station plays.
/// Jellyfin does not push updates of channel items to clients; the text shows on the next list load.
/// </summary>
public sealed class NowPlayingService : IHostedService, IDisposable
{
    /// <summary>
    /// Wait between checks while polling is disabled or the station URL is unset.
    /// </summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wait between checks of <see cref="IServerApplicationHost.CoreStartupHasCompleted"/> before the first poll,
    /// so the poller does not race Jellyfin's own channel refresh at startup.
    /// </summary>
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long StopAsync waits for the loop to finish.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly StationClient _stationClient;
    private readonly NowPlayingState _state;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<NowPlayingService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private bool _stationReachable = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class.
    /// DI resolves this constructor (the other one needs a <see cref="Func{T}"/> that is not registered).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger)
        : this(stationClient, state, libraryManager, applicationHost, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        Func<PluginConfiguration?> configuration)
    {
        _stationClient = stationClient;
        _state = state;
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await Task.WhenAny(_loop, Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host gave up waiting; the loop observes _stopping and ends on its own.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    /// <summary>
    /// Runs one poll and returns how long to wait before the next. Public for tests.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The delay before the next poll.</returns>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || !config.NowPlayingEnabled || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            if (_state.Clear())
            {
                await WriteOverviewAsync(config, null, cancellationToken).ConfigureAwait(false);
            }

            return IdleDelay;
        }

        NowPlayingFeed? feed;
        try
        {
            feed = await _stationClient.GetNowPlayingAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsStationFailure(ex))
        {
            if (_stationReachable)
            {
                _logger.LogWarning("Station {Host} is unreachable or not answering with JSON: {Message}", baseUrl.Host, ex.Message);
                _stationReachable = false;
            }

            if (_state.Clear())
            {
                await WriteOverviewAsync(config, null, cancellationToken).ConfigureAwait(false);
            }

            return TimeSpan.FromSeconds(config.EffectivePollSeconds);
        }

        if (!_stationReachable)
        {
            _logger.LogInformation("Station {Host} is answering again", baseUrl.Host);
            _stationReachable = true;
        }

        if (feed is not null && _state.Update(feed, DateTime.UtcNow))
        {
            await WriteOverviewAsync(config, _state.Description(), cancellationToken).ConfigureAwait(false);
        }

        return TimeSpan.FromSeconds(config.EffectivePollSeconds);
    }

    /// <summary>
    /// Exceptions that mean "the station did not give us a feed", as opposed to bugs.
    /// NotSupportedException is what ReadFromJsonAsync throws for a non-JSON content type (a proxy error page).
    /// </summary>
    private static bool IsStationFailure(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException;

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_applicationHost.CoreStartupHasCompleted)
            {
                await Task.Delay(StartupProbeDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Now-playing poller started");
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = IdleDelay;
            try
            {
                delay = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything; the next poll may succeed.
                _logger.LogError(ex, "Now-playing poll failed unexpectedly");
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Now-playing poller stopped");
    }

    private async Task WriteOverviewAsync(PluginConfiguration? config, string? description, CancellationToken cancellationToken)
    {
        var channel = FindChannel(config);
        if (channel is null)
        {
            _logger.LogDebug("Channel entity not created yet; nothing to update");
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ChannelIds = [channel.Id],
            ExternalId = JellywaveChannel.LiveItemId,
            IncludeItemTypes = [BaseItemKind.Audio]
        });

        var item = items.Count > 0 ? items[0] : null;
        if (item is null)
        {
            _logger.LogDebug("Live item not created yet; nothing to update");
            return;
        }

        var overview = description ?? JellywaveChannel.DefaultOverview;
        if (string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            return;
        }

        item.Overview = overview;
        await _libraryManager.UpdateItemAsync(item, channel, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        // Information on purpose: one line per track change is the runtime evidence that the poller works.
        _logger.LogInformation("Live item overview updated: {Overview}", overview);
    }

    /// <summary>
    /// Finds the channel entity by name without creating it (unlike IChannelManager.GetChannelsInternalAsync).
    /// </summary>
    private Channel? FindChannel(PluginConfiguration? config)
    {
        var name = JellywaveChannel.StationName(config ?? new PluginConfiguration());
        return _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Channel] })
            .OfType<Channel>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
