using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Polls every listed SUB/WAVE station's now-playing feed (card L4) and writes what it learns onto that station's item: the
/// one-line description into <see cref="BaseItem.Overview"/> and the current cover art as the Primary image through
/// <see cref="LiveItemArtwork"/>; plain streams get their logo through <see cref="StationLogos"/>. A poll has two phases:
/// the stations are fetched at most <see cref="MaxConcurrentStations"/> at a time (network only, every result kept under
/// its <see cref="StationKey"/>), then the items are reconciled one by one in grid order (one cheap query each, a write
/// only when a field differs), so an overwrite by Jellyfin's own channel refresh is repaired on the next poll. A failure,
/// an empty feed or a timeout at one station touches only that station's state, health and item.
/// Card L14: only the stations <see cref="StationDemand"/> calls needed are asked (someone plays one, or had its station
/// page open in the last minute), each at most once per poll interval; nothing is sent to an idle station, and its item
/// shows the station's own look (its tagline, else its notes, else the default text, and the logo; not recorded as a
/// failure). A station that becomes needed wakes the loop for a poll that asks only the stations not asked since they
/// became needed, so it is asked within a second or two; such a woken poll leaves the poll interval's timing as it was,
/// and once the interval is up the regular poll runs whatever wakes are pending.
/// Neither <c>Artists</c> nor <c>Album</c> is written: Artists would make Jellyfin create a Music-library
/// artist per track, and Album is part of the item's user-data key, so a rotating value would leave an
/// orphan user-data row per track and user (see DESIGN.md).
/// Jellyfin does not push updates of channel items to clients; changes show on the next list load.
/// </summary>
public sealed class NowPlayingService : IHostedService, IDisposable
{
    /// <summary>
    /// Most SUB/WAVE stations fetched at once. One station costs at most 32 s (feed 8 s, state 8 s, cover 8 s for the
    /// headers and 8 s for the body: the <see cref="StationClient"/> budgets), so the fetch phase takes at most
    /// ceil(stations / 4) times that, which is one station's worst case for up to four stations. The cap keeps a long list
    /// from opening dozens of connections at once, often to one host.
    /// </summary>
    internal const int MaxConcurrentStations = 4;

    /// <summary>
    /// A streak of slow fetch phases (longer than the poll interval) ends only below this share of the interval, so a fetch
    /// phase that jitters around the interval does not log a line every few polls.
    /// </summary>
    private const double SlowFetchEndRatio = 0.8;

    /// <summary>
    /// Format of the rank in an item's sort name: four digits, so up to 10 000 stations sort as numbers.
    /// </summary>
    private const string SortRankFormat = "D4";

    /// <summary>
    /// Shortest wait between two polls (card L14), so a burst of playback starts or page opens cannot run polls back to back.
    /// A station woken right after a poll is asked this much later.
    /// </summary>
    private static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Wait between checks while polling is disabled or no SUB/WAVE station is listed.
    /// </summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the stations played at the last item lookup that worked keep counting as played while the lookups fail
    /// (card L14 fix round): long enough for a library lock during a scan, bounded so a lookup that keeps failing does not
    /// keep a station needed for good.
    /// </summary>
    private static readonly TimeSpan LookupFailureHold = TimeSpan.FromMinutes(2);

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
    private readonly StationLogos _logos;
    private readonly LiveItemFinder _finder;
    private readonly PollerHealth _health;
    private readonly ScheduleCache _schedule;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<NowPlayingService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly WarningStreaks _failing = new();
    private readonly WarningStreaks _saveErrors = new();
    private readonly TimeProvider _clock;
    private readonly StationDemand _demand;

    // Card L14, used by the poll only (one at a time): the needed stations asked since they became needed (by origin), the
    // stations the last poll found needed, and those a session played at the last item lookup that worked.
    private readonly Dictionary<string, string> _asked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _needed = new(StringComparer.Ordinal);
    private HashSet<string> _lastPlaying = new(StringComparer.Ordinal);
    private long _lastPlayingAt;
    private Task? _loop;
    private bool _slowFetch;

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class.
    /// DI resolves this constructor (the other one needs a <see cref="Func{T}"/> that is not registered).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The cover keeper for the SUB/WAVE stations' items.</param>
    /// <param name="logos">The logo keeper for the plain streams' items.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="schedule">The schedule cache, whose unlisted stations the poller drops.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="demand">Which SUB/WAVE stations someone needs now (card L14).</param>
    public NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        StationLogos logos,
        LiveItemFinder finder,
        PollerHealth health,
        ScheduleCache schedule,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        StationDemand demand)
        : this(stationClient, state, artwork, logos, finder, health, schedule, libraryManager, applicationHost, logger, () => Plugin.Instance?.Configuration, TimeProvider.System, demand)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class with an explicit configuration source, for the
    /// tests of the fetch and write phases: every listed station counts as needed (<see cref="StationDemand.EveryStation"/>).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The cover keeper for the SUB/WAVE stations' items.</param>
    /// <param name="logos">The logo keeper for the plain streams' items.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="schedule">The schedule cache, whose unlisted stations the poller drops.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    internal NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        StationLogos logos,
        LiveItemFinder finder,
        PollerHealth health,
        ScheduleCache schedule,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        Func<PluginConfiguration?> configuration)
        : this(stationClient, state, artwork, logos, finder, health, schedule, libraryManager, applicationHost, logger, configuration, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class with an explicit configuration source and
    /// clock (tests; the clock times the fetch phase), every listed station counting as needed.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The cover keeper for the SUB/WAVE stations' items.</param>
    /// <param name="logos">The logo keeper for the plain streams' items.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="schedule">The schedule cache, whose unlisted stations the poller drops.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="clock">The clock.</param>
    internal NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        StationLogos logos,
        LiveItemFinder finder,
        PollerHealth health,
        ScheduleCache schedule,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        Func<PluginConfiguration?> configuration,
        TimeProvider clock)
        : this(stationClient, state, artwork, logos, finder, health, schedule, libraryManager, applicationHost, logger, configuration, clock, StationDemand.EveryStation())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NowPlayingService"/> class with an explicit configuration source, clock
    /// and demand (tests; the clock times the fetch phase and the poll interval of each station).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="artwork">The cover keeper for the SUB/WAVE stations' items.</param>
    /// <param name="logos">The logo keeper for the plain streams' items.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="health">The health snapshot for the configuration page.</param>
    /// <param name="schedule">The schedule cache, whose unlisted stations the poller drops.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="demand">Which SUB/WAVE stations someone needs now.</param>
    internal NowPlayingService(
        StationClient stationClient,
        NowPlayingState state,
        LiveItemArtwork artwork,
        StationLogos logos,
        LiveItemFinder finder,
        PollerHealth health,
        ScheduleCache schedule,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogger<NowPlayingService> logger,
        Func<PluginConfiguration?> configuration,
        TimeProvider clock,
        StationDemand demand)
    {
        _clock = clock;
        _demand = demand;
        _stationClient = stationClient;
        _state = state;
        _artwork = artwork;
        _logos = logos;
        _finder = finder;
        _health = health;
        _schedule = schedule;
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
    /// Runs one poll, asking every needed station, and returns how long to wait before the next. Public for tests.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The delay before the next poll.</returns>
    public Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken) => PollOnceAsync(false, cancellationToken);

    /// <summary>
    /// Runs one poll and returns how long to wait before the next. A woken poll (card L14: a station became needed) asks
    /// only the needed stations not asked since they became needed; the others keep their place in the poll interval.
    /// </summary>
    /// <param name="woken">Whether a station woke the loop before the poll interval was up.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The delay before the next poll.</returns>
    internal async Task<TimeSpan> PollOnceAsync(bool woken, CancellationToken cancellationToken)
    {
        var config = _configuration();
        var sources = config is null ? [] : StationSources.Publish(config);
        var subWave = sources.Where(s => s.Key is not null).ToList();
        ForgetUnlisted(sources);

        var polling = config is not null && config.NowPlayingEnabled;
        Dictionary<string, PreparedArtwork?> artwork;
        List<StationSource> asked = [];
        var idle = new HashSet<string>(StringComparer.Ordinal);
        if (polling)
        {
            // Resolved once more for the write phase; here to find the played items and to skip covers for items that do
            // not exist yet.
            var channel = ChannelForCovers(config!, out var channelKnown, cancellationToken);
            var needed = NeededStations(subWave, channel, channelKnown, cancellationToken);
            asked = subWave.Where(s => needed.Contains(s.Station.Id) && (!woken || !AskedSinceNeeded(s))).ToList();
            if (woken && asked.Count == 0 && !subWave.Any(s => !needed.Contains(s.Station.Id) && _asked.ContainsKey(s.Station.Id)))
            {
                // A wake with nobody new to ask (a start and stop between two polls, an item of another channel with a
                // station's id): no fetch and no write phase, so a stream of such wakes costs no item writes. Unless a
                // station went idle since its last ask (fix round 2): its idle look, idle mark and next wake start here.
                return TimeSpan.FromSeconds(config!.EffectivePollSeconds);
            }

            var started = _clock.GetTimestamp();
            artwork = await FetchStationsAsync(config!, asked, channel, cancellationToken).ConfigureAwait(false);
            // Only a regular poll's fetch phase is the list's (a woken one asks one or two stations), so only it is recorded
            // and only it can end a slow streak; one that asked nothing clears the time, so the page never shows an old one.
            if (!woken && asked.Count > 0)
            {
                RecordFetchTime(_clock.GetElapsedTime(started), config!, asked.Count);
            }
            else if (!woken)
            {
                _health.NothingAsked();
            }

            foreach (var source in subWave.Where(s => !needed.Contains(s.Station.Id)))
            {
                artwork[source.Station.Id] = StationIdle(source);
                idle.Add(source.Station.Id);
            }
        }
        else
        {
            // Polling off: every SUB/WAVE item falls back to the default overview and the embedded logo.
            _state.Clear();
            _asked.Clear();
            _needed.Clear();
            artwork = subWave.ToDictionary(s => s.Station.Id, s => (PreparedArtwork?)LiveItemArtwork.Logo(s.Station.Id), StringComparer.Ordinal);
        }

        foreach (var source in asked)
        {
            _asked[source.Station.Id] = source.Key!.Origin;
        }

        await UpdateItemsAsync(config, sources, artwork, idle, cancellationToken).ConfigureAwait(false);
        return polling && subWave.Count > 0 ? TimeSpan.FromSeconds(config!.EffectivePollSeconds) : IdleDelay;
    }

    /// <summary>
    /// The sort name for a place in the grid: the rank, zero-padded, then the station name for whoever reads it.
    /// </summary>
    /// <param name="rank">The place, from 0.</param>
    /// <param name="station">The station.</param>
    /// <returns>The sort name.</returns>
    internal static string SortNameOf(int rank, StationDefinition station)
        => rank.ToString(SortRankFormat, CultureInfo.InvariantCulture) + " " + JellywaveChannel.StationName(station);

    /// <summary>
    /// Makes the grid follow <see cref="StationDefinition.SortOrder"/>: Jellyfin lists channel items by <c>SortName</c>
    /// (jellyfin-web's default <c>IsFolder,SortName</c>, and the repository's default when a client names no order), and
    /// a channel item has no sort field of its own, so the item's <c>ForcedSortName</c> carries its place. Not part of
    /// the item's user-data key (unlike <c>IndexNumber</c> and <c>Name</c>), so changing it orphans nothing.
    /// </summary>
    /// <returns><see cref="ItemUpdateType.MetadataEdit"/> when the sort name changed.</returns>
    private static ItemUpdateType ReconcileSortName(BaseItem item, int rank, StationDefinition station)
    {
        var sortName = SortNameOf(rank, station);
        if (string.Equals(item.ForcedSortName, sortName, StringComparison.Ordinal))
        {
            return ItemUpdateType.None;
        }

        item.ForcedSortName = sortName;
        return ItemUpdateType.MetadataEdit;
    }

    /// <summary>
    /// Follows a station rename (card L3): Jellyfin applies a channel item's name only when it creates the item (12.3
    /// <c>ChannelManager.GetChannelItemEntityAsync</c>, <c>ChannelManager.cs:1027-1029</c>), so a renamed station would keep
    /// its old name on its card. <c>Name</c> is part of the item's user-data key (<c>Audio.GetUserDataKeys</c>,
    /// <c>Audio.cs:100-115</c>); a rename therefore moves that key once (see DESIGN "Station rename"). Written only when it
    /// differs, reconciled every poll like the sort name.
    /// </summary>
    /// <returns><see cref="ItemUpdateType.MetadataEdit"/> when the name changed.</returns>
    private ItemUpdateType ReconcileName(BaseItem item, StationDefinition station)
    {
        var name = JellywaveChannel.ItemName(station);
        if (string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            return ItemUpdateType.None;
        }

        _logger.LogInformation("Station {Name} item renamed", JellywaveChannel.StationName(station));
        item.Name = name;
        return ItemUpdateType.MetadataEdit;
    }

    /// <summary>
    /// Drops what is remembered of stations that are no longer listed (or changed kind), so every map stays bounded by the
    /// listed stations and a station that comes back starts afresh.
    /// </summary>
    private void ForgetUnlisted(IReadOnlyList<StationSource> sources)
    {
        var subWaveIds = sources.Where(s => s.Key is not null).Select(s => s.Station.Id).ToHashSet(StringComparer.Ordinal);
        var streamIds = sources.Where(s => s.Key is null).Select(s => s.Station.Id).ToHashSet(StringComparer.Ordinal);
        _state.Retain(subWaveIds);
        _health.Retain(subWaveIds);
        _schedule.Retain(subWaveIds);
        _artwork.Retain(subWaveIds);
        _failing.Retain(subWaveIds);
        foreach (var id in _asked.Keys.Where(id => !subWaveIds.Contains(id)).ToList())
        {
            _asked.Remove(id);
        }

        _logos.Retain(streamIds);
        _saveErrors.Retain(sources.Select(s => s.Station.Id).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Card L14: the SUB/WAVE stations someone needs now (<see cref="StationDemand.Decide"/>), with an Information line when a
    /// station starts or stops being needed (not for the stations that are idle from the start).
    /// </summary>
    private IReadOnlySet<string> NeededStations(IReadOnlyList<StationSource> subWave, Channel? channel, bool channelKnown, CancellationToken cancellationToken)
    {
        var needed = _demand.Decide(subWave.Select(s => s.Station.Id).ToList(), PlayingStations(subWave, channel, channelKnown, cancellationToken));
        foreach (var source in subWave)
        {
            var id = source.Station.Id;
            if (needed.Contains(id) && _needed.Add(id))
            {
                _logger.LogInformation("Station {Name} is in use (someone plays it or has its page open); asking it for now playing", JellywaveChannel.StationName(source.Station));
            }
            else if (!needed.Contains(id) && _needed.Remove(id))
            {
                _logger.LogInformation("Station {Name} is idle (nobody plays it or has its page open); not asked until someone does", JellywaveChannel.StationName(source.Station));
            }
        }

        _needed.IntersectWith(needed);
        return needed;
    }

    /// <summary>
    /// The SUB/WAVE stations whose item a session plays (paused included), counting only the sessions of users who can see
    /// the tile (<see cref="StationDemand.PlayingItemIds"/>, given the channel this poll resolved). The items are looked up
    /// only when something plays. When there is no channel of ours (not created yet, or a name collision), none of our items
    /// can play. When the channel or a lookup is unknown (a library lock during a scan) and something plays, the stations
    /// played at the last lookup that worked count as still played, so a lock never makes a listened station idle, for at
    /// most <see cref="LookupFailureHold"/>.
    /// </summary>
    private HashSet<string> PlayingStations(IReadOnlyList<StationSource> subWave, Channel? channel, bool channelKnown, CancellationToken cancellationToken)
    {
        try
        {
            if (channel is not null)
            {
                var playing = _demand.PlayingItemIds(channel);
                return Played(playing.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : subWave.Where(s => _finder.FindLiveItem(channel, s.Station.Id) is { } item && playing.Contains(item.Id))
                        .Select(s => s.Station.Id)
                        .ToHashSet(StringComparer.Ordinal));
            }

            if (channelKnown || !_demand.AnythingPlays())
            {
                return Played(new HashSet<string>(StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Item lookup before the station fetches failed; the stations played at the last lookup count as played");
        }

        if (_clock.GetElapsedTime(_lastPlayingAt) > LookupFailureHold)
        {
            _lastPlaying = new HashSet<string>(StringComparer.Ordinal);
        }

        return _lastPlaying;
    }

    private HashSet<string> Played(HashSet<string> stations)
    {
        _lastPlaying = stations;
        _lastPlayingAt = _clock.GetTimestamp();
        return stations;
    }

    /// <summary>
    /// Whether a needed station was asked since it became needed, under its current origin (a changed base URL is another
    /// station). A woken poll skips those: they are asked on the poll interval as before.
    /// </summary>
    private bool AskedSinceNeeded(StationSource source)
        => _asked.TryGetValue(source.Station.Id, out var origin) && string.Equals(origin, source.Key!.Origin, StringComparison.Ordinal);

    /// <summary>
    /// Card L14: a station nobody needs is not asked. Its item falls back to the station's own look (HI Q2: its own text,
    /// <see cref="JellywaveChannel.IdleOverview"/>, and the logo, a SUB/WAVE station having no logo field), and its next
    /// need asks it at once. Not a failure: no failing health, no warning.
    /// </summary>
    private PreparedArtwork StationIdle(StationSource source)
    {
        var id = source.Station.Id;
        _asked.Remove(id);
        _health.Idle(source.Key!, DateTime.UtcNow);
        _state.Clear(id);
        return LiveItemArtwork.Logo(id);
    }

    /// <summary>
    /// The fetch phase: every SUB/WAVE station asked this poll, at most <see cref="MaxConcurrentStations"/> at a time. Network
    /// only; the items are not touched here. Each station's outcome is the artwork its item should get.
    /// </summary>
    private async Task<Dictionary<string, PreparedArtwork?>> FetchStationsAsync(PluginConfiguration config, IReadOnlyList<StationSource> stations, Channel? channel, CancellationToken cancellationToken)
    {
        var outcomes = new PreparedArtwork?[stations.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStations, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(
            Enumerable.Range(0, stations.Count),
            options,
            async (index, token) => outcomes[index] = await FetchStationAsync(config, stations[index], channel, token).ConfigureAwait(false)).ConfigureAwait(false);

        var artwork = new Dictionary<string, PreparedArtwork?>(StringComparer.Ordinal);
        for (var index = 0; index < stations.Count; index++)
        {
            artwork[stations[index].Station.Id] = outcomes[index];
        }

        return artwork;
    }

    /// <summary>
    /// Fetches one station's feed, its <c>/api/state</c> and the cover its item should show. Throws only on cancellation:
    /// whatever else goes wrong is this station's failure alone.
    /// </summary>
    /// <returns>The artwork for the station's item; null to leave its image as it is.</returns>
    private async Task<PreparedArtwork?> FetchStationAsync(PluginConfiguration config, StationSource source, Channel? channel, CancellationToken cancellationToken)
    {
        var station = source.Station;
        var key = source.Key!;
        var baseUrl = source.BaseUrl!;
        try
        {
            NowPlayingFeed? feed;
            try
            {
                feed = await _stationClient.GetNowPlayingAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && StationFailures.IsStationFailure(ex))
            {
                return StationFailed(key, station, StationFailures.Category(ex));
            }

            if (feed is null)
            {
                // JSON null parses without an error but carries nothing; keeping the last feed would show a stale track.
                return StationFailed(key, station, StationFailures.EmptyFeed);
            }

            if (_failing.End(station.Id))
            {
                _logger.LogInformation("Station {Name} is answering again", JellywaveChannel.StationName(station));
            }

            _health.Success(key, DateTime.UtcNow);
            _state.Update(key, feed, DateTime.UtcNow);
            await FetchStationStateAsync(station, key, baseUrl, cancellationToken).ConfigureAwait(false);

            // No item yet (the channel was never listed, or a collision), or the lookup failed: nothing would be saved, so
            // nothing is downloaded and the item keeps its image.
            if (!ItemExists(channel, station, cancellationToken))
            {
                return null;
            }

            return await PrepareArtworkAsync(config, station, key, baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A bug must not stop the other stations; this one shows its logo and the default text until it is fixed.
            _logger.LogError(ex, "Polling station {Name} failed unexpectedly", JellywaveChannel.StationName(station));
            _health.Failure(key, DateTime.UtcNow, StationFailures.Unexpected);
            _state.Clear(station.Id);
            return LiveItemArtwork.Logo(station.Id);
        }
    }

    /// <summary>
    /// The channel entity for the fetch phase's item check, or null when it is unknown: not created yet, a collision, or
    /// the lookup failed (a library lock during a scan). Unknown only skips the cover downloads; the stations are still
    /// fetched, and the write phase resolves the channel again.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="known">False when the lookup failed; true when the channel was found, is missing or collides.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel, or null.</returns>
    private Channel? ChannelForCovers(PluginConfiguration config, out bool known, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = _finder.Resolve(config);
            known = true;
            return resolution.Collides ? null : resolution.Channel;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Channel lookup before the station fetches failed; no cover is downloaded this poll");
            known = false;
            return null;
        }
    }

    /// <summary>
    /// Whether the station's item exists, for the fetch phase. A failing lookup counts as "unknown", not as a station
    /// failure: the station answered, so its health and state stay as they are and its item keeps its image this poll.
    /// </summary>
    private bool ItemExists(Channel? channel, StationDefinition station, CancellationToken cancellationToken)
    {
        if (channel is null)
        {
            return false;
        }

        try
        {
            return _finder.FindLiveItem(channel, station.Id) is not null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Item lookup of station {Name} failed; its cover is not downloaded this poll", JellywaveChannel.StationName(station));
            return false;
        }
    }

    /// <summary>
    /// The station gave no feed (unreachable, not JSON, or an empty feed): record it, warn once per streak, forget the
    /// station's state and fall its item back to the default overview and the logo. Touches no other station.
    /// </summary>
    private PreparedArtwork StationFailed(StationKey key, StationDefinition station, string category)
    {
        _health.Failure(key, DateTime.UtcNow, category);

        // The streak is the station's origin: a base URL changed to another failing one warns again.
        if (_failing.Begin(station.Id, key.Origin))
        {
            _logger.LogWarning("Station {Name} is unreachable or not answering with a feed: {Reason}", JellywaveChannel.StationName(station), category);
        }

        _state.Clear(station.Id);
        return LiveItemArtwork.Logo(station.Id);
    }

    /// <summary>
    /// The station page shows history, up next and the lyrics flag from <c>/api/state</c>; fetched alongside the
    /// feed, and a failure keeps the previous state (the feed already proved the station answers). The previous state
    /// is the same station's: <see cref="NowPlayingState.Update"/> dropped it when the feed came from another origin.
    /// </summary>
    private async Task FetchStationStateAsync(StationDefinition station, StationKey key, Uri baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var stationState = await _stationClient.GetStateAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (stationState is not null)
            {
                _state.UpdateStationState(key, stationState);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && StationFailures.IsStationFailure(ex))
        {
            _logger.LogDebug("State fetch of station {Name} failed: {Reason}", JellywaveChannel.StationName(station), StationFailures.Category(ex));
        }
    }

    /// <summary>
    /// Downloads the station's current cover when its item does not show it yet (before the item is read, so the item is
    /// held for milliseconds only). A failing download keeps the item's image and is tried again next poll, except when the
    /// item's cover is another origin's (the base URL changed): then the logo stands in, so the old art never sits beside
    /// the new station's text.
    /// </summary>
    private async Task<PreparedArtwork?> PrepareArtworkAsync(PluginConfiguration config, StationDefinition station, StationKey key, Uri baseUrl, CancellationToken cancellationToken)
    {
        var coverId = config.CoverArtEnabled ? _state.Snapshot(key).CoverId : null;
        try
        {
            return await _artwork.PrepareAsync(station.Id, baseUrl, coverId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && StationFailures.IsStationFailure(ex))
        {
            _logger.LogDebug("Cover art fetch of station {Name} failed: {Reason}", JellywaveChannel.StationName(station), StationFailures.Category(ex));
            return string.Equals(key.Origin, _artwork.AppliedOrigin(station.Id), StringComparison.Ordinal) ? null : LiveItemArtwork.Logo(station.Id);
        }
    }

    /// <summary>
    /// Records how long the fetch phase took (Health's <c>LastFetchSeconds</c>) and logs one Information line per streak of
    /// fetch phases that alone took longer than the poll interval: no deadline cuts a poll short (each call has its own
    /// budget), so a slow list only delays the next poll, and the admin should know. The streak ends below
    /// <see cref="SlowFetchEndRatio"/> of the interval.
    /// </summary>
    private void RecordFetchTime(TimeSpan elapsed, PluginConfiguration config, int stations)
    {
        _health.FetchTook(elapsed);
        var interval = TimeSpan.FromSeconds(config.EffectivePollSeconds);
        if (elapsed <= interval)
        {
            if (elapsed < interval * SlowFetchEndRatio)
            {
                _slowFetch = false;
            }

            return;
        }

        if (!_slowFetch)
        {
            _slowFetch = true;
            _logger.LogInformation(
                "The fetch phase alone took {Seconds:0.0} s for {Count} SUB/WAVE stations, longer than the poll interval of {Interval} s; the items update less often than set",
                elapsed.TotalSeconds,
                stations,
                config.EffectivePollSeconds);
        }
    }

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

        // Card L14: the last poll that was not woken, and the wait it asked for; a woken poll keeps that timing.
        var regularEnd = _clock.GetTimestamp();
        var regularDelay = IdleDelay;
        var woken = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = IdleDelay;
            try
            {
                delay = await PollOnceAsync(woken, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything; the next poll may succeed. Each station's own failures are caught and
                // recorded for that station (FetchStationAsync, UpdateItemsAsync); what reaches here belongs to no station.
                _logger.LogError(ex, "Now-playing poll failed unexpectedly");
            }

            if (!woken)
            {
                regularEnd = _clock.GetTimestamp();
                regularDelay = delay;
            }

            try
            {
                // Card L14: a station that becomes needed ends the wait early (a playback start of its item, its page's
                // first call), but never sooner than WakeGap after a poll.
                // Once the interval is up the regular poll runs, wake or not (fix round R1): a stream of wakes must not
                // replace it with woken polls, which skip the stations already asked.
                await Task.Delay(WakeGap, cancellationToken).ConfigureAwait(false);
                var left = regularDelay - _clock.GetElapsedTime(regularEnd);
                woken = left > TimeSpan.Zero && await _demand.WaitForWakeAsync(left, cancellationToken).ConfigureAwait(false);
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
    /// The write phase: reconciles every listed item in grid order. A SUB/WAVE station's item gets its own state (overview)
    /// and the artwork its fetch prepared (cover or logo); a plain stream's item gets its logo; every item gets its place as
    /// its sort name. Writes only what differs; most polls write nothing. One item's failure does not stop the others.
    /// </summary>
    private async Task UpdateItemsAsync(PluginConfiguration? config, IReadOnlyList<StationSource> sources, Dictionary<string, PreparedArtwork?> artwork, IReadOnlySet<string> idle, CancellationToken cancellationToken)
    {
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

        _logos.BeginPoll();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (source.Key is { } key)
                {
                    await UpdateSubWaveItemAsync(channel, source, key, artwork.GetValueOrDefault(source.Station.Id), idle.Contains(source.Station.Id), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await UpdateStreamItemAsync(channel, source, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Updating station {Name}'s item failed", JellywaveChannel.StationName(source.Station));
            }
        }
    }

    /// <summary>
    /// A listed SUB/WAVE station: its sort name, its own now-playing line (the default text after a failure, an empty feed
    /// or with polling off, when its state was cleared; while it is idle, its own text, card L14) and the artwork its fetch
    /// prepared.
    /// </summary>
    private async Task UpdateSubWaveItemAsync(Channel channel, StationSource source, StationKey key, PreparedArtwork? artwork, bool idle, CancellationToken cancellationToken)
    {
        var station = source.Station;
        var item = _finder.FindLiveItem(channel, station.Id);
        if (item is null)
        {
            _logger.LogDebug("Item of station {Name} not created yet; nothing to update", JellywaveChannel.StationName(station));
            return;
        }

        var updateType = ReconcileSortName(item, source.Rank, station) | ReconcileName(item, station);
        var overview = idle ? JellywaveChannel.IdleOverview(station) : _state.Snapshot(key).Description ?? JellywaveChannel.DefaultOverview;
        var overviewChanged = !string.Equals(item.Overview, overview, StringComparison.Ordinal);
        if (overviewChanged)
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
                    _logger.LogInformation("Station {Name} item cover updated: {Cover}", JellywaveChannel.StationName(station), artwork.Key);
                }

                EndSaveErrors(station);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // Saving the file failed (metadata folder not writable, disk full). Keep the text update; log once per streak.
                if (_saveErrors.Begin(station.Id, "cover"))
                {
                    _logger.LogError(ex, "Saving station {Name}'s cover failed; the item keeps its current image", JellywaveChannel.StationName(station));
                }
            }
        }

        if (updateType == ItemUpdateType.None)
        {
            return;
        }

        await _libraryManager.UpdateItemAsync(item, channel, updateType, cancellationToken).ConfigureAwait(false);
        if (overviewChanged)
        {
            // Information on purpose: one line per track change is the runtime evidence that the poller works.
            _logger.LogInformation("Station {Name} item overview updated: {Overview}", JellywaveChannel.StationName(station), overview);
        }
    }

    /// <summary>
    /// A listed plain stream: its sort name, its overview (its notes, card L8) and its logo (its own logo, else the embedded one). The item is read first only
    /// to see whether it exists and has an image; a logo download then happens before the item is read again, so the
    /// instance written is held for milliseconds.
    /// </summary>
    private async Task UpdateStreamItemAsync(Channel channel, StationSource source, CancellationToken cancellationToken)
    {
        var station = source.Station;
        var existing = _finder.FindLiveItem(channel, station.Id);
        if (existing is null)
        {
            return;
        }

        var logo = await _logos.PrepareAsync(station, existing.HasImage(ImageType.Primary), cancellationToken).ConfigureAwait(false);
        var item = logo is null ? existing : _finder.FindLiveItem(channel, station.Id);
        if (item is null)
        {
            return;
        }

        var updateType = ReconcileSortName(item, source.Rank, station) | ReconcileName(item, station);

        // Jellyfin applies a channel item's overview only when it creates the item (ChannelManager, 12.3), so an id that was
        // a SUB/WAVE station before keeps its last now-playing line, and edited notes would never reach the item; a plain
        // stream shows its notes, else the default text (card L8).
        var overview = JellywaveChannel.StreamOverview(station);
        if (!string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            item.Overview = overview;
            updateType |= ItemUpdateType.MetadataEdit;
        }

        if (logo is not null)
        {
            try
            {
                if (await _logos.ApplyAsync(item, logo, cancellationToken).ConfigureAwait(false))
                {
                    updateType |= ItemUpdateType.ImageUpdate;
                    _logger.LogInformation("Station {Name} item image updated: {Image}", JellywaveChannel.StationName(station), logo.Image is null ? "embedded logo" : "station logo");
                }

                EndSaveErrors(station);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // Metadata folder not writable, disk full: the downloaded logo joins the negative cache instead of being
                // downloaded again every poll. Logged once per station and streak.
                _logos.SaveFailed(logo);
                if (_saveErrors.Begin(station.Id, "logo"))
                {
                    _logger.LogError(ex, "Saving station {Name}'s logo failed; the item keeps its current image", JellywaveChannel.StationName(station));
                }
            }
        }

        if (updateType != ItemUpdateType.None)
        {
            await _libraryManager.UpdateItemAsync(item, channel, updateType, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EndSaveErrors(StationDefinition station)
    {
        if (_saveErrors.End(station.Id))
        {
            _logger.LogInformation("Saving station {Name}'s image works again", JellywaveChannel.StationName(station));
        }
    }
}
