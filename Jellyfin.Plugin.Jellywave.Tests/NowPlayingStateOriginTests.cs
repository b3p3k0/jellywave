using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 (D2): <see cref="NowPlayingState"/> holds one station's data. A feed from another origin drops the station
/// state learned from the previous one, and a state from another origin than the current feed's is not stored.
/// </summary>
public class NowPlayingStateOriginTests
{
    private const string OriginA = "http://station-a.example/";
    private const string OriginB = "http://station-b.example/radio/";

    private static NowPlayingFeed Feed(string title) => new() { NowPlaying = new TrackInfo { Artist = "A", Title = title }, StreamOnline = true };

    private static StationState History(string title) => new() { History = new List<QueueTrack> { new() { Artist = "Old", Title = title } } };

    [Fact]
    public void A_feed_from_the_same_origin_keeps_the_station_state()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        Assert.True(state.UpdateStationState(OriginA, History("a1")));

        state.Update(OriginA, Feed("two"), DateTime.UtcNow);

        Assert.Equal("a1", state.StationState()?.History?[0].Title);
    }

    [Fact]
    public void A_feed_from_another_origin_clears_the_station_state()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        state.UpdateStationState(OriginA, History("a1"));

        state.Update(OriginB, Feed("b"), DateTime.UtcNow);

        Assert.Null(state.StationState());
        Assert.Equal("b", state.Feed()?.NowPlaying?.Title);
    }

    [Fact]
    public void A_state_from_another_origin_than_the_feed_is_not_stored()
    {
        var state = new NowPlayingState();
        state.Update(OriginB, Feed("b"), DateTime.UtcNow);

        Assert.False(state.UpdateStationState(OriginA, History("a1")));
        Assert.Null(state.StationState());
        Assert.True(state.UpdateStationState(OriginB, History("b1")));
        Assert.Equal("b1", state.StationState()?.History?[0].Title);
    }

    [Fact]
    public void After_clear_no_state_is_stored_until_a_feed_arrives()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        state.Clear();

        Assert.False(state.UpdateStationState(OriginA, History("a1")));
        Assert.Null(state.StationState());
    }

    [Fact]
    public void A_snapshot_carries_the_origin_with_the_feed_and_the_state()
    {
        var state = new NowPlayingState();
        Assert.Null(state.Snapshot().Origin);
        state.Update(OriginA, new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "A", Title = "one", SubsonicId = "c1" }, StreamOnline = true }, DateTime.UtcNow);
        state.UpdateStationState(OriginA, History("a1"));

        var snapshot = state.Snapshot();

        Assert.Equal((OriginA, "one", "a1", "c1", "A – one"), (snapshot.Origin, snapshot.Feed?.NowPlaying?.Title, snapshot.StationState?.History?[0].Title, snapshot.CoverId, snapshot.TrackLine));
        Assert.Equal(state.Description(), snapshot.Description);
        state.Clear();
        Assert.Null(state.Snapshot().Origin);
    }
}
