using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L1: until L2 everything reads one station, <see cref="PluginConfiguration.PrimaryStation"/>: the first enabled
/// SUB/WAVE station by sort order, list order breaking ties.
/// </summary>
public class PrimaryStationTests
{
    private static StationDefinition Station(string id, bool enabled = true, StationKind kind = StationKind.SubWave, int sort = 0)
        => new() { Id = id, Enabled = enabled, Kind = kind, SortOrder = sort, BaseUrl = "http://" + id + ".example" };

    [Fact]
    public void A_disabled_station_is_skipped()
    {
        var config = new PluginConfiguration { Stations = { Station("off", enabled: false), Station("on", sort: 5) } };
        Assert.Equal("on", config.PrimaryStation()!.Id);
    }

    [Fact]
    public void A_plain_stream_is_skipped()
    {
        var config = new PluginConfiguration { Stations = { Station("stream", kind: StationKind.Stream), Station("subwave", sort: 5) } };
        Assert.Equal("subwave", config.PrimaryStation()!.Id);
    }

    [Fact]
    public void The_lowest_sort_order_wins_over_list_order()
    {
        var config = new PluginConfiguration { Stations = { Station("second", sort: 2), Station("first", sort: 1) } };
        Assert.Equal("first", config.PrimaryStation()!.Id);
    }

    [Fact]
    public void List_order_breaks_a_tie()
    {
        var config = new PluginConfiguration { Stations = { Station("a", sort: 1), Station("b", sort: 1) } };
        Assert.Equal("a", config.PrimaryStation()!.Id);
    }

    [Fact]
    public void No_eligible_station_means_none()
    {
        var config = new PluginConfiguration { Stations = { Station("off", enabled: false), Station("stream", kind: StationKind.Stream), null! } };
        Assert.Null(config.PrimaryStation());
        Assert.Null(new PluginConfiguration { Stations = null! }.PrimaryStation());
    }

    [Fact]
    public void A_candidate_the_tile_does_not_list_is_no_primary_station()
    {
        // Card L2 (RA R1): a stream ahead of it took the id "live", so the SUB/WAVE station is refused as a duplicate.
        var stream = new StationDefinition { Id = "live", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3", SortOrder = 0 };
        var subwave = Station("live", sort: 1);
        var config = new PluginConfiguration { Stations = { stream, subwave } };

        Assert.Same(subwave, config.PrimaryCandidate());
        Assert.Null(config.PrimaryStation());
        Assert.Equal(StationSources.RefusedDuplicateId, StationSources.NotListedReason(config, subwave));
    }

    [Fact]
    public void A_candidate_with_a_refused_url_is_no_primary_station()
    {
        var refused = Station("live");
        refused.BaseUrl = "http://radio.example/?q=1";
        var config = new PluginConfiguration { Stations = { refused, Station("other", sort: 1) } };

        Assert.Same(refused, config.PrimaryCandidate());
        Assert.Null(config.PrimaryStation());
        Assert.False(StationUrls.TryGetBaseUrl(config, out _));
    }

    [Fact]
    public void A_listed_candidate_is_the_primary_station()
    {
        var config = new PluginConfiguration { Stations = { Station("live") } };

        Assert.Same(config.Stations[0], config.PrimaryStation());
    }
}
