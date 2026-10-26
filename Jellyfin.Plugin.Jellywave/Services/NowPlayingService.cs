using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Polls the station's now-playing feed and writes a one-line description onto the live item.
/// Only <see cref="BaseItem.Overview"/> is written: setting Artists on an Audio item would make
/// Jellyfin create a Music-library artist entry for every track the station plays.
/// </summary>
public sealed class NowPlayingService : IHostedService, IDisposable
{
    /// <summary>
    /// Wait between checks while polling is disabled or the station URL is unset.
    /// </summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long StopAsync waits for the loop to finish.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly StationClient _stationClient;
    private readonly NowPlayingState _state;
    private readonly IChannelManager _channelManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<NowPlayingService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private bool _stationReachable = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="channelManager">The channel manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        IChannelManager channelManager,
        ILibraryManager libraryManager,
        ILogger<NowPlayingService> logger)
    {
        _stationClient = stationClient;
        _state = state;
        _channelManager = channelManager;
        _libraryManager = libraryManager;
        _logger = logger;
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
            await Task.WhenAny(_loop, Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Runs one poll and returns how long to wait before the next.
    /// </summary>
    private async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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

    private async Task WriteOverviewAsync(PluginConfiguration? config, string? description, CancellationToken cancellationToken)
    {
        var channel = await FindChannelAsync(config).ConfigureAwait(false);
        if (channel is null)
        {
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
        _logger.LogDebug("Live item overview updated: {Overview}", overview);
    }

    private async Task<Channel?> FindChannelAsync(PluginConfiguration? config)
    {
        var name = JellywaveChannel.StationName(config ?? new PluginConfiguration());
        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        return channels.Items.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
