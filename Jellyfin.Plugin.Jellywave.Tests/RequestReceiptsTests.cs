using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Listener request receipts: only the submitting user may poll, receipts expire, and the map is capped. Card R2a: the
/// receipt is our own random token, mapped to the user, the station key and the station's request id; a slot is reserved
/// before the request is sent; at most <see cref="RequestReceipts.MaxPerUser"/> per user, the user's own oldest first,
/// never another user's.
/// </summary>
public class RequestReceiptsTests
{
    private static readonly StationKey Station = StationKey.Of("live", new Uri("http://station.example/"));
    private static readonly StationKey Other = StationKey.Of("two", new Uri("http://two.example/"));
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Owner_passes_and_learns_the_station()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var token = receipts.Record(Alice, Station, "req_1")!;
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(token, Alice, out var station));
        Assert.Equal(Station, station);
        Assert.Equal("req_1", receipts.StationRequestIdOf(token, Alice, Station));
    }

    [Fact]
    public void Another_user_is_denied()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var token = receipts.Record(Alice, Station, "req_1")!;
        Assert.Equal(ReceiptOwnership.OtherUser, receipts.Check(token, Bob, out var station));
        Assert.Null(station);
        Assert.Null(receipts.StationRequestIdOf(token, Bob, Station));
    }

    [Fact]
    public void The_token_is_ours_random_and_never_the_station_s_id()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var tokens = Enumerable.Range(0, 50).Select(_ => receipts.Record(Alice, Station, "req_1")!).ToList();

        Assert.All(tokens, t => Assert.Equal(RequestReceipts.TokenLength, t.Length));
        Assert.All(tokens, t => Assert.True(StationClient.IsValidId(t), t));
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check("req_1", Alice, out _));   // the station's id is no receipt
    }

    [Fact]
    public void The_same_station_request_id_from_two_stations_for_two_users_never_crosses()
    {
        // Two stations both answer "req_1": Alice's at one, Bob's at the other. Each token reaches only its own.
        var receipts = new RequestReceipts(new ManualClock());
        var alice = receipts.Record(Alice, Station, "req_1")!;
        var bob = receipts.Record(Bob, Other, "req_1")!;
        Assert.NotEqual(alice, bob);

        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(alice, Alice, out var aliceStation));
        Assert.Equal(Station, aliceStation);
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(bob, Bob, out var bobStation));
        Assert.Equal(Other, bobStation);

        Assert.Equal(ReceiptOwnership.OtherUser, receipts.Check(bob, Alice, out _));
        Assert.Equal(ReceiptOwnership.OtherUser, receipts.Check(alice, Bob, out _));
        Assert.Null(receipts.StationRequestIdOf(bob, Alice, Other));
        Assert.Null(receipts.StationRequestIdOf(alice, Alice, Other));   // the token's own station key only
        Assert.Equal("req_1", receipts.StationRequestIdOf(alice, Alice, Station));
    }

    [Fact]
    public void The_same_station_request_id_from_two_stations_for_one_user_keeps_each_station()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var first = receipts.Record(Alice, Station, "req_1")!;
        var second = receipts.Record(Alice, Other, "req_1")!;

        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(first, Alice, out var one));
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(second, Alice, out var two));
        Assert.Equal((Station, Other), (one, two));
    }

    [Theory]
    [InlineData("req_2")]
    [InlineData("")]
    public void Tokens_never_issued_here_are_unknown(string token)
    {
        var receipts = new RequestReceipts(new ManualClock());
        receipts.Record(Alice, Station, "req_1");
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(token, Alice, out _));
        Assert.Null(receipts.StationRequestIdOf(token, Alice, Station));
    }

    [Fact]
    public void A_token_in_other_case_is_unknown()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var token = receipts.Record(Alice, Station, "req_1")!;
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(token.ToUpperInvariant(), Alice, out _));
    }

    [Fact]
    public void A_reserved_slot_is_unknown_until_completed_and_a_cancelled_one_is_gone()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var token = receipts.Reserve(Alice)!;
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(token, Alice, out _));
        Assert.Equal(1, receipts.Count);

        Assert.False(receipts.Complete(token, Bob, Station, "req_1"));   // not Bob's slot
        Assert.True(receipts.Complete(token, Alice, Station, "req_1"));
        Assert.False(receipts.Complete(token, Alice, Other, "req_2"));   // filled once
        Assert.Equal("req_1", receipts.StationRequestIdOf(token, Alice, Station));

        receipts.Cancel(token);   // an accepted receipt is not cancelled
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(token, Alice, out _));

        var unused = receipts.Reserve(Alice)!;
        receipts.Cancel(unused);
        Assert.Equal(1, receipts.Count);
        Assert.False(receipts.Complete(unused, Alice, Station, "req_3"));
    }

    [Fact]
    public void Expired_receipt_is_unknown()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        var token = receipts.Record(Alice, Station, "req_1")!;
        clock.Now += RequestReceipts.Lifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(token, Alice, out _));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(receipts.StationRequestIdOf(token, Alice, Station));
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(token, Alice, out _));
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(token, Bob, out _));
    }

    [Fact]
    public void An_unfilled_reservation_expires_and_cannot_be_filled_late()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        var token = receipts.Reserve(Alice)!;
        clock.Now += RequestReceipts.ReservationLifetime;
        Assert.False(receipts.Complete(token, Alice, Station, "req_1"));
        receipts.Reserve(Bob);
        Assert.Equal(1, receipts.Count);
    }

    [Fact]
    public void Reserve_prunes_expired_receipts()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        receipts.Record(Alice, Station, "req_1");
        receipts.Record(Bob, Station, "req_2");
        clock.Now += RequestReceipts.Lifetime;
        receipts.Record(Alice, Station, "req_3");
        Assert.Equal(1, receipts.Count);
    }

    [Fact]
    public void A_user_holds_at_most_twenty_and_gives_up_their_own_oldest()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        var bob = receipts.Record(Bob, Station, "bob")!;
        var alice = new List<string>();
        for (var i = 0; i < RequestReceipts.MaxPerUser + 3; i++)
        {
            alice.Add(receipts.Record(Alice, Station, "req_" + i)!);
            clock.Now += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(RequestReceipts.MaxPerUser + 1, receipts.Count);
        Assert.All(alice.Take(3), t => Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(t, Alice, out _)));
        Assert.All(alice.Skip(3), t => Assert.Equal(ReceiptOwnership.Owner, receipts.Check(t, Alice, out _)));
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check(bob, Bob, out _));   // never another user's
    }

    [Fact]
    public void A_full_store_evicts_the_caller_s_own_oldest_and_refuses_a_caller_who_owns_none()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        var owners = Enumerable.Range(0, RequestReceipts.MaxEntries / RequestReceipts.MaxPerUser).Select(_ => Guid.NewGuid()).ToList();
        var first = new Dictionary<Guid, string>();
        foreach (var owner in owners)
        {
            for (var i = 0; i < RequestReceipts.MaxPerUser; i++)
            {
                var token = receipts.Record(owner, Station, "req_" + i)!;
                first.TryAdd(owner, token);
                clock.Now += TimeSpan.FromMilliseconds(10);
            }
        }

        Assert.Equal(RequestReceipts.MaxEntries, receipts.Count);

        // A newcomer owns none: refused before anything is sent, and nobody's receipt goes.
        Assert.Null(receipts.Reserve(Alice));
        Assert.Equal(RequestReceipts.MaxEntries, receipts.Count);
        Assert.All(first, f => Assert.Equal(ReceiptOwnership.Owner, receipts.Check(f.Value, f.Key, out _)));

        // An owner gives up their own oldest.
        var next = receipts.Record(owners[5], Station, "req_new");
        Assert.NotNull(next);
        Assert.Equal(RequestReceipts.MaxEntries, receipts.Count);
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(first[owners[5]], owners[5], out _));
        Assert.All(first.Where(f => f.Key != owners[5]), f => Assert.Equal(ReceiptOwnership.Owner, receipts.Check(f.Value, f.Key, out _)));
    }

    [Fact]
    public void A_user_whose_slots_are_all_reserved_is_refused()
    {
        var receipts = new RequestReceipts(new ManualClock());
        for (var i = 0; i < RequestReceipts.MaxPerUser; i++)
        {
            Assert.NotNull(receipts.Reserve(Alice));
        }

        Assert.Null(receipts.Reserve(Alice));
        Assert.NotNull(receipts.Reserve(Bob));
    }

    [Fact]
    public void Concurrent_reserves_and_checks_stay_consistent()
    {
        var receipts = new RequestReceipts(new ManualClock());
        var users = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToArray();
        Parallel.For(0, 4000, i =>
        {
            var user = users[i % users.Length];
            var token = receipts.Record(user, Station, "req_" + i);
            if (token is not null)
            {
                Assert.NotEqual(ReceiptOwnership.OtherUser, receipts.Check(token, user, out _));
            }
        });
        Assert.InRange(receipts.Count, 1, RequestReceipts.MaxEntries);
    }
}
