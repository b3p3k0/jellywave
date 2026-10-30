using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Which SUB/WAVE stations someone needs now (card L14), so the now-playing poller asks only those and nothing leaves the
/// server for the others. A station is needed while a session plays its item (paused included, through
/// <see cref="IPlayingItems"/>) and for <see cref="ListenerGrace"/> after, and for <see cref="PageWindow"/> after a signed-in
/// user's last station route call for it (<see cref="PageOpened"/>; the open station page asks its feed every few
/// seconds). Every other station is idle. A station that goes from idle to needed (a page's first call, a playback start
/// of its item) wakes the poller (<see cref="WaitForWakeAsync"/>), so its track shows within a second or two instead of
/// after the poll interval. Only a user who can see the tile counts (fix round S2): a session's playback, and a playback
/// start, count only when the session's user passes <see cref="StationAccess.SessionUserGate()"/> (for a start) or
/// <see cref="StationAccess.SessionUserGate(Channel)"/> (for a poll, with the channel it resolved), the same tile gate the
/// station routes use (a page call passed it already). Thread-safe; <see cref="Decide"/> drops the stations that are no
/// longer listed, so every map holds at most one entry per listed station.
/// </summary>
public sealed class StationDemand : IDisposable
{
    /// <summary>
    /// How long a station counts as needed after the last station route call for it: the open station page asks its feed
    /// every 5 s, so a page closed for this long is gone (HI 2026-10-29: "in the last 60 s").
    /// </summary>
    internal static readonly TimeSpan PageWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a station counts as needed after a session was last seen playing it: a stop and a new start (a client
    /// reconnecting, a listener coming back) does not flip the item to the station's own look and back, and one failed
    /// item lookup (a library lock during a scan) does not either. Short, so an idle station stops being asked soon after.
    /// </summary>
    internal static readonly TimeSpan ListenerGrace = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly IPlayingItems _playing;
    private readonly TimeProvider _clock;
    private readonly Func<Func<Guid, bool>> _tileGate;
    private readonly Func<Channel, Func<Guid, bool>> _channelGate;
    private readonly bool _everyStation;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, long> _pageSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _heard = new(StringComparer.Ordinal);
    private readonly HashSet<string> _idle = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="StationDemand"/> class (DI constructor).
    /// </summary>
    /// <param name="playing">The items Jellyfin's sessions play, and their playback starts.</param>
    /// <param name="access">The station routes' tile gate, applied to the sessions' users.</param>
    public StationDemand(IPlayingItems playing, StationAccess access)
        : this(playing, TimeProvider.System, access.SessionUserGate, access.SessionUserGate, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationDemand"/> class for tests: every user can see the tile.
    /// </summary>
    /// <param name="playing">The items Jellyfin's sessions play, and their playback starts.</param>
    internal StationDemand(IPlayingItems playing)
        : this(playing, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationDemand"/> class with an explicit clock (tests): every user can
    /// see the tile.
    /// </summary>
    /// <param name="playing">The items Jellyfin's sessions play, and their playback starts.</param>
    /// <param name="clock">The clock; only its timestamps are read.</param>
    internal StationDemand(IPlayingItems playing, TimeProvider clock)
        : this(playing, clock, Everyone, _ => Everyone(), false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationDemand"/> class with an explicit clock and tile gate (tests).
    /// </summary>
    /// <param name="playing">The items Jellyfin's sessions play, and their playback starts.</param>
    /// <param name="clock">The clock; only its timestamps are read.</param>
    /// <param name="tileGate">
    /// Returns the check whether a session's user can see the tile; may throw (a library lock). Serves the poll's channel
    /// too (the channel a test's poll passes is the test's own).
    /// </param>
    internal StationDemand(IPlayingItems playing, TimeProvider clock, Func<Func<Guid, bool>> tileGate)
        : this(playing, clock, tileGate, _ => tileGate(), false)
    {
    }

    private StationDemand(IPlayingItems playing, TimeProvider clock, Func<Func<Guid, bool>> tileGate, Func<Channel, Func<Guid, bool>> channelGate, bool everyStation)
    {
        _playing = playing;
        _clock = clock;
        _tileGate = tileGate;
        _channelGate = channelGate;
        _everyStation = everyStation;
        _playing.PlaybackStarted += OnPlaybackStarted;
    }

    /// <summary>
    /// A demand that counts every listed station as needed, for the tests of the poll's fetch and write phases (they test
    /// what a poll does with a station it asks, not which stations it asks). Never registered.
    /// </summary>
    /// <returns>The demand.</returns>
    internal static StationDemand EveryStation() => new(new NobodyPlaying(), TimeProvider.System, Everyone, _ => Everyone(), true);

    /// <summary>
    /// Records a signed-in user's station route call for a listed SUB/WAVE station (the station route filter calls it, after
    /// the channel gate and the station lookup). An idle station wakes the poller.
    /// </summary>
    /// <param name="stationId">The station id the filter resolved.</param>
    public void PageOpened(string stationId)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        bool wake;
        lock (_gate)
        {
            _pageSeen[stationId] = _clock.GetTimestamp();
            wake = _idle.Remove(stationId);
        }

        if (wake)
        {
            Wake();
        }
    }

    /// <summary>
    /// Gets the ids of the items the sessions of users who can see the tile are playing now (paused included), for the
    /// channel the poll resolved already. One pass over the sessions; the gate is built at the first session that plays
    /// something and asked once per user. It throws when a user lookup fails (the poller then keeps the last lookup).
    /// </summary>
    /// <param name="channel">The tile's channel entity, resolved by the caller (not a collision).</param>
    /// <returns>The item ids; empty when nothing plays.</returns>
    public IReadOnlySet<Guid> PlayingItemIds(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Func<Guid, bool>? sees = null;
        var users = new Dictionary<Guid, bool>();
        return _playing.PlayingItemIds(user => users.TryGetValue(user, out var known) ? known : users[user] = (sees ??= _channelGate(channel))(user));
    }

    /// <summary>
    /// Gets whether any session plays anything, whoever its user: when the channel lookup fails, nothing playing at all
    /// still means none of the stations plays.
    /// </summary>
    /// <returns>True when a session plays an item.</returns>
    public bool AnythingPlays() => _playing.PlayingItemIds().Count > 0;

    /// <summary>
    /// The poller's decision, once per poll: which of the listed SUB/WAVE stations are needed. The others are idle until a
    /// page call or a playback start of their item wakes the poller. Forgets the stations not in the list.
    /// </summary>
    /// <param name="stationIds">The listed SUB/WAVE stations' ids.</param>
    /// <param name="playing">The ids of those whose item a session plays now.</param>
    /// <returns>The needed stations' ids.</returns>
    public IReadOnlySet<string> Decide(IReadOnlyCollection<string> stationIds, IReadOnlySet<string> playing)
    {
        ArgumentNullException.ThrowIfNull(stationIds);
        ArgumentNullException.ThrowIfNull(playing);
        var now = _clock.GetTimestamp();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var listed = new HashSet<string>(stationIds, StringComparer.Ordinal);
            Retain(_pageSeen, listed);
            Retain(_heard, listed);
            _idle.RemoveWhere(id => !listed.Contains(id));
            foreach (var id in listed)
            {
                if (playing.Contains(id))
                {
                    _heard[id] = now;
                }

                if (_everyStation || Within(_heard, id, now, ListenerGrace) || Within(_pageSeen, id, now, PageWindow))
                {
                    needed.Add(id);
                    _idle.Remove(id);
                }
                else
                {
                    _idle.Add(id);
                }
            }
        }

        return needed;
    }

    /// <summary>
    /// Waits until an idle station becomes needed or the timeout ends, whichever comes first. Wakes that come while nobody
    /// waits are kept as one, so the poll after them sees them.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when a station woke the poller; false when the timeout ended.</returns>
    public Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _wake.WaitAsync(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _playing.PlaybackStarted -= OnPlaybackStarted;
        _wake.Dispose();
    }

    private static Func<Guid, bool> Everyone() => _ => true;

    private static void Retain(Dictionary<string, long> map, HashSet<string> listed)
    {
        foreach (var id in new List<string>(map.Keys))
        {
            if (!listed.Contains(id))
            {
                map.Remove(id);
            }
        }
    }

    // Under _gate.
    private bool Within(Dictionary<string, long> map, string id, long now, TimeSpan window)
        => map.TryGetValue(id, out var at) && _clock.GetElapsedTime(at, now) <= window;

    /// <summary>
    /// A playback start of an idle station's item by a user who can see the tile wakes the poller. The external id is the
    /// station id (card L2); another channel's item with the same external id costs one early poll that asks nothing (and
    /// writes nothing). Runs on Jellyfin's event: never throws, and a gate that fails wakes nothing (the next regular poll
    /// sees the playback anyway).
    /// </summary>
    private void OnPlaybackStarted(object? sender, PlaybackStartedEventArgs e)
    {
        if (e?.ExternalId is not { } id)
        {
            return;
        }

        lock (_gate)
        {
            if (!_idle.Contains(id))
            {
                return;
            }
        }

        try
        {
            var sees = _tileGate();
            if (!(e.UserIds ?? []).Any(sees))
            {
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        bool wake;
        lock (_gate)
        {
            wake = _idle.Remove(id);
        }

        if (wake)
        {
            Wake();
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already waiting; one poll serves both.
        }
        catch (ObjectDisposedException)
        {
            // Server shutting down.
        }
    }

    /// <summary>The playing view of <see cref="EveryStation"/>: nothing plays, nothing starts.</summary>
    private sealed class NobodyPlaying : IPlayingItems
    {
        public event EventHandler<PlaybackStartedEventArgs>? PlaybackStarted
        {
            add { }
            remove { }
        }

        public IReadOnlySet<Guid> PlayingItemIds() => new HashSet<Guid>();

        public IReadOnlySet<Guid> PlayingItemIds(Func<Guid, bool> userCounts) => new HashSet<Guid>();
    }
}
