using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Card R2a (HI decision D10): how many station calls each Jellyfin user may cause, so one user cannot spend a station's
/// own limits (it sees this whole server as one caller) or load it for everyone. Per user, in sliding windows:
/// <see cref="CallsPerMinute"/> user-driven station calls of every kind; song requests <see cref="RequestsPerMinute"/> per
/// minute and <see cref="RequestsPerHour"/> per hour across all stations; likes <see cref="LikesPerMinute"/> per minute. A
/// call is checked against every window it counts in and reserved in all of them in one step under one lock, before the
/// station is called; over any of them it is refused (the route answers 429 with <c>Retry-After</c>: when the fullest
/// window frees a slot). A station's own lower limit still wins: it refuses on its own. A call that never left (or that
/// this server's limiter refused) is given back in full (<see cref="BudgetReservation.Refund"/>); a station's refusal gives
/// back the kind's own windows only and keeps the general one spent (<see cref="BudgetReservation.RefundOwnWindows"/>);
/// an uncertain outcome gives back nothing. In memory, reset on restart (accepted); users with nothing left in any window
/// are dropped every <see cref="SweepInterval"/>.
/// </summary>
public sealed class UserBudgets
{
    /// <summary>User-driven station calls of every kind per user per minute. The station page's normal use stays under a third.</summary>
    public const int CallsPerMinute = 60;

    /// <summary>Song requests per user per minute, across all stations.</summary>
    public const int RequestsPerMinute = 1;

    /// <summary>Song requests per user per hour, across all stations.</summary>
    public const int RequestsPerHour = 4;

    /// <summary>Likes per user per minute.</summary>
    public const int LikesPerMinute = 10;

    /// <summary>How often users with empty windows are dropped.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private const int CallsWindow = 0;
    private const int RequestMinuteWindow = 1;
    private const int RequestHourWindow = 2;
    private const int LikeMinuteWindow = 3;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Each window's limit and length, by index.</summary>
    private static readonly (int Limit, TimeSpan Length)[] Windows =
    [
        (CallsPerMinute, Minute),
        (RequestsPerMinute, Minute),
        (RequestsPerHour, Hour),
        (LikesPerMinute, Minute)
    ];

    private static readonly int[] CallsOnly = [CallsWindow];
    private static readonly int[] RequestWindows = [CallsWindow, RequestMinuteWindow, RequestHourWindow];
    private static readonly int[] LikeWindows = [CallsWindow, LikeMinuteWindow];

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<DateTimeOffset>[]> _users = new();
    private DateTimeOffset _lastSweep;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserBudgets"/> class (system clock).
    /// </summary>
    public UserBudgets()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserBudgets"/> class with an explicit clock (tests).
    /// </summary>
    /// <param name="clock">The clock.</param>
    internal UserBudgets(TimeProvider clock)
    {
        _clock = clock;
        _lastSweep = clock.GetUtcNow();
    }

    /// <summary>Gets the number of users held (tests: the sweep drops those with nothing left).</summary>
    internal int UserCount
    {
        get
        {
            lock (_gate)
            {
                return _users.Count;
            }
        }
    }

    /// <summary>
    /// Checks one call against every window it counts in and, when all have room, reserves it in all of them at once.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="kind">What the call is.</param>
    /// <param name="retryAfter">When refused: how long until every window it counts in has room; otherwise zero.</param>
    /// <returns>The reservation, or null when a window is full.</returns>
    public BudgetReservation? TryReserve(Guid userId, StationCallKind kind, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var windows = kind switch
        {
            StationCallKind.Request => RequestWindows,
            StationCallKind.Like => LikeWindows,
            _ => CallsOnly
        };
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            if (!_users.TryGetValue(userId, out var lists))
            {
                lists = Enumerable.Range(0, Windows.Length).Select(_ => new List<DateTimeOffset>()).ToArray();
                _users[userId] = lists;
            }

            foreach (var w in windows)
            {
                var (limit, length) = Windows[w];
                var list = lists[w];
                list.RemoveAll(t => t <= now - length);
                if (list.Count >= limit)
                {
                    // The slot frees when the oldest entry that keeps the window full leaves it.
                    var frees = list.Order().ElementAt(list.Count - limit) + length - now;
                    retryAfter = frees > retryAfter ? frees : retryAfter;
                }
            }

            if (retryAfter > TimeSpan.Zero)
            {
                return null;
            }

            foreach (var w in windows)
            {
                lists[w].Add(now);
            }

            return new BudgetReservation(this, userId, windows, now);
        }
    }

    /// <summary>Takes one reservation back out of the windows it counts in (<see cref="BudgetReservation"/>).</summary>
    /// <param name="reservation">The reservation.</param>
    /// <param name="includeGeneral">Whether the general window gets it back.</param>
    /// <param name="includeOwn">Whether the kind's own windows get it back.</param>
    internal void Refund(BudgetReservation reservation, bool includeGeneral, bool includeOwn)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(reservation.UserId, out var lists))
            {
                return;
            }

            foreach (var w in reservation.Windows.Where(w => w == CallsWindow ? includeGeneral : includeOwn))
            {
                lists[w].Remove(reservation.At);
            }
        }
    }

    /// <summary>Drops users with nothing left in any window, at most every <see cref="SweepInterval"/>.</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
        foreach (var (user, lists) in _users.ToList())
        {
            if (lists.Select((list, w) => list.All(t => t <= now - Windows[w].Length)).All(empty => empty))
            {
                _users.Remove(user);
            }
        }
    }
}
