using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L2: <see cref="LiveItemFinder.FindLiveItem"/> finds a station's item by the station id, which is the channel
/// item's external id.
/// </summary>
public class LiveItemFinderStationTests
{
    private static (LiveItemFinder Finder, FakeLibrary Library, Channel Channel) Build()
    {
        var library = FakeLibrary.Create();
        library.ListedItems.Add(new Audio { Id = Guid.NewGuid(), ExternalId = "live", Name = "Primary" });
        library.ListedItems.Add(new Audio { Id = Guid.NewGuid(), ExternalId = "c07e", Name = "Plain" });
        return (new LiveItemFinder(library.Manager, () => [], NullLogger<LiveItemFinder>.Instance), library, new Channel { Id = Guid.NewGuid(), Name = "Live Radio" });
    }

    [Fact]
    public void Each_station_id_finds_its_own_item()
    {
        var (finder, _, channel) = Build();

        Assert.Equal("Primary", finder.FindLiveItem(channel, "live")?.Name);
        Assert.Equal("Plain", finder.FindLiveItem(channel, "c07e")?.Name);
        Assert.Null(finder.FindLiveItem(channel, "unknown"));
    }

    [Fact]
    public void The_query_names_the_channel_the_external_id_and_audio()
    {
        var (finder, library, channel) = Build();

        finder.FindLiveItem(channel, "c07e");

        var query = Assert.Single(library.Queries);
        Assert.Equal("c07e", query.ExternalId);
        Assert.Equal(new[] { channel.Id }, query.ChannelIds);
        Assert.Equal(new[] { BaseItemKind.Audio }, query.IncludeItemTypes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_station_id_finds_nothing_without_a_query(string? stationId)
    {
        var (finder, library, channel) = Build();

        Assert.Null(finder.FindLiveItem(channel, stationId));
        Assert.Empty(library.Queries);
    }
}
