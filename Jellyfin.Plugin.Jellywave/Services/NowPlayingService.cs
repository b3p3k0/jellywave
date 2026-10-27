using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Polls the station's now-playing feed and writes what it learns onto the live item: the one-line
/// description into <see cref="BaseItem.Overview"/> and the current cover art as the Primary image
/// through <see cref="LiveItemArtwork"/>. Every successful poll reconciles the item (one cheap query),
/// so an overwrite by Jellyfin's own channel refresh is repaired on the next poll.
/// Neither <c>Artists</c> nor <c>Album</c> is written: Artists would make Jellyfin create a Music-library
/// artist per track, and Album is part of the item's user-data key, so a rotating value would leave an
/// orphan user-data row per track and user (see DESIGN.md).
/// Jellyfin does not push updates of channel items to clients; changes show on the next list load.
/// </summary>
public sealed class NowPlayingService : IHostedService, IDisposable
{
    /// <summary>
    /// The health message for a feed that parsed as JSON <c>null</c>: the station answered, but with nothing to show.
    /// </summary>
    internal const string EmptyFeedMessage = "the station answered with an empty feed";

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
    private readonly LiveItemArtwork _artwork;
    private readonly LiveItemFinder _finder;
    private readonly PollerHealth _health;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<NowPlayingService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private bool _stationReachable = true;
    private bool _artworkSaveFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class.
    /// DI resolves this constructor (the other one needs a <see cref="Func{T}"/> that is not registered).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The live item artwork keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        LiveItemFinder finder,
        PollerHealth health,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger)
        : this(stationClient, state, artwork, finder, health, libraryManager, applicationHost, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The live item artwork keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        LiveItemFinder finder,
        PollerHealth health,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        Func<PluginConfiguration?> configuration)
    {
        _stationClient = stationClient;
        _state = state;
        _artwork = artwork;
        _finder = finder;
        _health = health;
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
            _state.Clear();
            await UpdateLiveItemAsync(config, null, cancellationToken).ConfigureAwait(false);
            return IdleDelay;
        }

        NowPlayingFeed? feed;
        try
        {
            feed = await _stationClient.GetNowPlayingAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsStationFailure(ex))
        {
            return await StationFailedAsync(config, baseUrl, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        if (feed is null)
        {
            // JSON null parses without an error but carries nothing; keeping the last feed would show a stale track.
            return await StationFailedAsync(config, baseUrl, EmptyFeedMessage, cancellationToken).ConfigureAwait(false);
        }

        if (!_stationReachable)
        {
            _logger.LogInformation("Station {Host} is answering again", baseUrl.Host);
            _stationReachable = true;
        }

        var origin = StationUrls.Origin(baseUrl);
        _health.Success(DateTime.UtcNow);
        _state.Update(origin, feed, DateTime.UtcNow);
        await FetchStationStateAsync(baseUrl, origin, cancellationToken).ConfigureAwait(false);
        await UpdateLiveItemAsync(config, baseUrl, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromSeconds(config.EffectivePollSeconds);
    }

    /// <summary>
    /// The station gave no feed (unreachable, not JSON, or an empty feed): record it, warn once per streak, forget
    /// the state and fall the live item back to the default overview and the logo.
    /// </summary>
    private async Task<TimeSpan> StationFailedAsync(PluginConfiguration config, Uri baseUrl, string message, CancellationToken cancellationToken)
    {
        _health.Failure(DateTime.UtcNow, message);
        if (_stationReachable)
        {
            _logger.LogWarning("Station {Host} is unreachable or not answering with a feed: {Message}", baseUrl.Host, message);
            _stationReachable = false;
        }

        _state.Clear();
        await UpdateLiveItemAsync(config, null, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromSeconds(config.EffectivePollSeconds);
    }

    /// <summary>
    /// The station page shows history, up next and the lyrics flag from <c>/api/state</c>; fetched alongside the
    /// feed, and a failure keeps the previous state (the feed already proved the station answers). The previous state
    /// is the same station's: <see cref="NowPlayingState.Update"/> dropped it when the feed came from another origin.
    /// </summary>
    private async Task FetchStationStateAsync(Uri baseUrl, string origin, CancellationToken cancellationToken)
    {
        try
        {
            var stationState = await _stationClient.GetStateAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (stationState is not null)
            {
                _state.UpdateStationState(origin, stationState);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsStationFailure(ex))
        {
            _logger.LogDebug("Station state fetch failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Exceptions that mean "the station did not give us a feed", as opposed to bugs.
    /// A non-JSON 200 (a proxy error page) surfaces as JsonException on .NET 10 and as
    /// NotSupportedException on runtimes that validate the content type; catch the family.
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
        _health.SetRunning(true);
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
                _health.Failure(DateTime.UtcNow, ex.GetType().Name + ": " + ex.Message);
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

        _health.SetRunning(false);
        _logger.LogInformation("Now-playing poller stopped");
    }

    /// <summary>
    /// Reconciles the live item with the current state: overview and cover. With <paramref name="baseUrl"/>
    /// null (polling disabled or the station unreachable) the item falls back to the default overview and
    /// the logo. Writes only what differs; most polls write nothing.
    /// </summary>
    private async Task UpdateLiveItemAsync(PluginConfiguration? config, Uri? baseUrl, CancellationToken cancellationToken)
    {
        // The cover download (up to 8 s) happens before the item is read, so the item is held for milliseconds only.
        var coverId = baseUrl is not null && config?.CoverArtEnabled == true ? _state.CoverId() : null;
        PreparedArtwork? artwork = null;
        try
        {
            artwork = await _artwork.PrepareAsync(baseUrl, coverId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsStationFailure(ex))
        {
            // The feed answered but the cover did not; the text still goes out and the cover is tried again next poll.
            _logger.LogDebug("Cover art fetch failed: {Message}", ex.Message);
            if (baseUrl is not null && !string.Equals(StationUrls.Origin(baseUrl), _artwork.AppliedOrigin, StringComparison.Ordinal))
            {
                // The item's cover is not this station's (a station switch): the logo stands in rather than leaving the
                // old station's art beside the new station's text.
                artwork = new PreparedArtwork(null, LiveItemArtwork.LogoKey, null);
            }
        }

        // A collision withholds the entity (it may be another plugin's channel), so nothing below is written.
        var resolution = _finder.Resolve(config);
        var channel = resolution.Channel;
        if (resolution.Collides)
        {
            _logger.LogDebug("Channel name collides with another plugin's channel; nothing to update");
            return;
        }

        if (channel is null)
        {
            _logger.LogDebug("Channel entity not created yet; nothing to update");
            return;
        }

        var item = _finder.FindLiveItem(channel);
        if (item is null)
        {
            _logger.LogDebug("Live item not created yet; nothing to update");
            return;
        }

        var updateType = ItemUpdateType.None;
        var overview = (baseUrl is null ? null : _state.Description()) ?? JellywaveChannel.DefaultOverview;
        if (!string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            item.Overview = overview;
            updateType |= ItemUpdateType.MetadataEdit;
        }

        if (artwork is not null)
        {
            try
            {
                if (await _artwork.ApplyAsync(item, artwork, cancellationToken).ConfigureAwait(false))
                {
                    updateType |= ItemUpdateType.ImageUpdate;
                    _logger.LogInformation("Live item cover updated: {Cover}", _artwork.Applied);
                }

                _artworkSaveFailed = false;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // Saving the file failed (metadata folder not writable, disk full). Keep the text update; log once per streak.
                if (!_artworkSaveFailed)
                {
                    _logger.LogError(ex, "Saving the live item's cover failed; the item keeps its current image");
                    _artworkSaveFailed = true;
                }
            }
        }

        if (updateType == ItemUpdateType.None)
        {
            return;
        }

        await _libraryManager.UpdateItemAsync(item, channel, updateType, cancellationToken).ConfigureAwait(false);
        if (updateType.HasFlag(ItemUpdateType.MetadataEdit))
        {
            // Information on purpose: one line per track change is the runtime evidence that the poller works.
            _logger.LogInformation("Live item overview updated: {Overview}", overview);
        }
    }
}
