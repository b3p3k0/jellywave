using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a (HI decision D10): per-user budgets, checked and reserved in one step before a station is called. 60 calls a
/// minute of every kind; requests 1 a minute and 4 an hour; likes 10 a minute. A refund gives a call back to every window
/// it counted in.
/// </summary>
public class UserBudgetsTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Sixty_calls_a_minute_then_refused_until_the_oldest_leaves_the_window()
    {
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        for (var i = 0; i < UserBudgets.CallsPerMinute; i++)
        {
            Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Lyrics, out _));
            clock.Now += TimeSpan.FromMilliseconds(500);
        }

        Assert.Null(budgets.TryReserve(Alice, StationCallKind.LikeStatus, out var wait));
        Assert.Equal(TimeSpan.FromSeconds(30), wait);   // the first call was 30 s ago
        Assert.NotNull(budgets.TryReserve(Bob, StationCallKind.LikeStatus, out _));   // another user's budget

        clock.Now += wait;
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Avatar, out _));
        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Avatar, out _));
    }

    [Fact]
    public void One_request_a_minute_and_four_an_hour_across_all_stations()
    {
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Request, out _));
        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Request, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);

        for (var i = 1; i < UserBudgets.RequestsPerHour; i++)
        {
            clock.Now += TimeSpan.FromMinutes(1);
            Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Request, out _));
        }

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Request, out wait));
        Assert.Equal(TimeSpan.FromMinutes(56), wait);   // the hour's first request leaves at 60 min
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.RequestStatus, out _));   // polls are general calls only

        clock.Now += wait;
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Request, out _));
    }

    [Fact]
    public void Ten_likes_a_minute_while_like_status_still_counts_in_the_general_budget_only()
    {
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        for (var i = 0; i < UserBudgets.LikesPerMinute; i++)
        {
            Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Like, out _));
        }

        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Like, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.LikeStatus, out _));
    }

    [Fact]
    public void A_refusal_reserves_nothing_in_the_windows_that_had_room()
    {
        // Checked in full before anything is reserved: a request over its own window does not eat a general slot.
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Request, out _));
        for (var i = 0; i < 100; i++)
        {
            Assert.Null(budgets.TryReserve(Alice, StationCallKind.Request, out _));
        }

        for (var i = 1; i < UserBudgets.CallsPerMinute; i++)
        {
            Assert.NotNull(budgets.TryReserve(Alice, StationCallKind.Lyrics, out _));
        }

        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Lyrics, out _));
    }

    [Fact]
    public void A_refund_gives_the_call_back_to_every_window_once()
    {
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        var first = budgets.TryReserve(Alice, StationCallKind.Request, out _)!;

        first.Refund();
        first.Refund();

        var second = budgets.TryReserve(Alice, StationCallKind.Request, out _);
        Assert.NotNull(second);
        Assert.Null(budgets.TryReserve(Alice, StationCallKind.Request, out _));   // the double refund gave back one only
    }

    [Fact]
    public void Concurrent_callers_get_exactly_the_budget()
    {
        var budgets = new UserBudgets(new ManualClock());
        var granted = 0;
        Parallel.For(0, 1000, i =>
        {
            if (budgets.TryReserve(Alice, StationCallKind.Lyrics, out _) is not null)
            {
                System.Threading.Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(UserBudgets.CallsPerMinute, granted);
    }

    [Fact]
    public void Users_with_nothing_left_are_dropped()
    {
        var clock = new ManualClock();
        var budgets = new UserBudgets(clock);
        foreach (var user in Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()))
        {
            budgets.TryReserve(user, StationCallKind.Lyrics, out _);
        }

        Assert.Equal(30, budgets.UserCount);
        clock.Now += UserBudgets.SweepInterval;
        budgets.TryReserve(Alice, StationCallKind.Lyrics, out _);
        Assert.Equal(1, budgets.UserCount);

        // A user with a request still inside its hour is kept.
        budgets.TryReserve(Bob, StationCallKind.Request, out _);
        clock.Now += UserBudgets.SweepInterval;
        budgets.TryReserve(Alice, StationCallKind.Lyrics, out _);
        Assert.Equal(2, budgets.UserCount);
    }
}
