using System;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Listener request receipts: only the submitting user may poll, receipts expire, and the map is capped.
/// </summary>
public class RequestReceiptsTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Owner_passes()
    {
        var receipts = new RequestReceipts(new ManualClock());
        receipts.Record("req_1", Alice);
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check("req_1", Alice));
    }

    [Fact]
    public void Another_user_is_denied()
    {
        var receipts = new RequestReceipts(new ManualClock());
        receipts.Record("req_1", Alice);
        Assert.Equal(ReceiptOwnership.OtherUser, receipts.Check("req_1", Bob));
    }

    [Theory]
    [InlineData("req_2")]
    [InlineData("REQ_1")]
    [InlineData("")]
    public void Ids_never_issued_here_are_unknown(string requestId)
    {
        var receipts = new RequestReceipts(new ManualClock());
        receipts.Record("req_1", Alice);
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check(requestId, Alice));
    }

    [Fact]
    public void Expired_receipt_is_unknown()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        receipts.Record("req_1", Alice);
        clock.Now += RequestReceipts.Lifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check("req_1", Alice));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check("req_1", Alice));
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check("req_1", Bob));
    }

    [Fact]
    public void Insert_prunes_expired_receipts()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        receipts.Record("req_1", Alice);
        receipts.Record("req_2", Bob);
        clock.Now += RequestReceipts.Lifetime;
        receipts.Record("req_3", Alice);
        Assert.Equal(1, receipts.Count);
    }

    [Fact]
    public void Cap_is_enforced_by_dropping_the_oldest()
    {
        var clock = new ManualClock();
        var receipts = new RequestReceipts(clock);
        for (var i = 0; i < RequestReceipts.MaxEntries + 5; i++)
        {
            receipts.Record("req_" + i.ToString(CultureInfo.InvariantCulture), Alice);
            clock.Now += TimeSpan.FromMilliseconds(10);
        }

        Assert.Equal(RequestReceipts.MaxEntries, receipts.Count);
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check("req_0", Alice));
        Assert.Equal(ReceiptOwnership.Unknown, receipts.Check("req_4", Alice));
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check("req_5", Alice));
        Assert.Equal(ReceiptOwnership.Owner, receipts.Check("req_" + (RequestReceipts.MaxEntries + 4).ToString(CultureInfo.InvariantCulture), Alice));
    }

    [Fact]
    public void Concurrent_inserts_and_checks_stay_consistent()
    {
        var receipts = new RequestReceipts(new ManualClock());
        Parallel.For(0, 4000, i =>
        {
            var id = "req_" + i.ToString(CultureInfo.InvariantCulture);
            receipts.Record(id, Alice);
            Assert.NotEqual(ReceiptOwnership.OtherUser, receipts.Check(id, Alice));
        });
        Assert.Equal(RequestReceipts.MaxEntries, receipts.Count);
    }
}
