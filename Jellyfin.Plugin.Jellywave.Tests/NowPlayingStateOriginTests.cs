using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 (D2) and card L4: <see cref="NowPlayingState"/> holds one entry per station id, keyed with its origin. A feed
/// under another origin for the same id drops the station state learned from the previous one, a state under another key
/// than the current feed's is not stored, a reader with the new key sees nothing, and other stations are never touched.
/// </summary>
public class NowPlayingStateOriginTests
{
    private static readonly StationKey OriginA = new("live", "http://station-a.example/");
    private static readonly StationKey OriginB = new("live", "http://station-b.example/radio/");
    private static readonly StationKey Other = new("other", "http://station-a.example/");

    private static NowPlayingFeed Feed(string title) => new() { NowPlaying = new TrackInfo { Artist = "A", Title = title }, StreamOnline = true };

    private static StationState History(string title) => new() { History = new List<QueueTrack> { new() { Artist = "Old", Title = title } } };

    [Fact]
    public void A_feed_from_the_same_origin_keeps_the_station_state()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        Assert.True(state.UpdateStationState(OriginA, History("a1")));

        state.Update(OriginA, Feed("two"), DateTime.UtcNow);

        Assert.Equal("a1", state.Snapshot(OriginA).StationState?.History?[0].Title);
    }

    [Fact]
    public void A_feed_from_another_origin_clears_the_station_state()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        state.UpdateStationState(OriginA, History("a1"));

        state.Update(OriginB, Feed("b"), DateTime.UtcNow);

        Assert.Null(state.Snapshot(OriginB).StationState);
        Assert.Equal("b", state.Snapshot(OriginB).Feed?.NowPlaying?.Title);
    }

    [Fact]
    public void A_state_from_another_origin_than_the_feed_is_not_stored()
    {
        var state = new NowPlayingState();
        state.Update(OriginB, Feed("b"), DateTime.UtcNow);

        Assert.False(state.UpdateStationState(OriginA, History("a1")));
        Assert.Null(state.Snapshot(OriginB).StationState);
        Assert.True(state.UpdateStationState(OriginB, History("b1")));
        Assert.Equal("b1", state.Snapshot(OriginB).StationState?.History?[0].Title);
    }

    [Fact]
    public void After_clear_no_state_is_stored_until_a_feed_arrives()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);
        state.Clear("live");

        Assert.False(state.UpdateStationState(OriginA, History("a1")));
        Assert.Null(state.Snapshot(OriginA).StationState);
    }

    [Fact]
    public void A_snapshot_carries_the_feed_the_state_and_the_derived_lines()
    {
        var state = new NowPlayingState();
        Assert.Same(NowPlayingSnapshot.Nothing, state.Snapshot(OriginA));
        state.Update(OriginA, new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "A", Title = "one", SubsonicId = "c1" }, StreamOnline = true }, DateTime.UtcNow);
        state.UpdateStationState(OriginA, History("a1"));

        var snapshot = state.Snapshot(OriginA);

        Assert.Equal(("one", "a1", "c1", "A – one"), (snapshot.Feed?.NowPlaying?.Title, snapshot.StationState?.History?[0].Title, snapshot.CoverId, snapshot.TrackLine));
        Assert.Equal("Now playing: A – one", snapshot.Description);
        Assert.False(snapshot.OtherOrigin);
        state.Clear();
        Assert.Same(NowPlayingSnapshot.Nothing, state.Snapshot(OriginA));
    }

    [Fact]
    public void A_reader_with_the_new_origin_sees_nothing_until_the_new_origin_is_heard()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("one"), DateTime.UtcNow);

        var snapshot = state.Snapshot(OriginB);

        Assert.Same(NowPlayingSnapshot.OtherStation, snapshot);
        Assert.True(snapshot.OtherOrigin);
        Assert.Null(snapshot.Feed);
        Assert.Null(snapshot.Description);
    }

    [Fact]
    public void Stations_keep_their_own_entries()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("live one"), DateTime.UtcNow);
        state.UpdateStationState(OriginA, History("live history"));
        state.Update(Other, Feed("other one"), DateTime.UtcNow);
        state.UpdateStationState(Other, History("other history"));

        // Another origin for "live" and a cleared "live" leave "other" alone.
        state.Update(OriginB, Feed("live two"), DateTime.UtcNow);
        state.Clear("live");

        var other = state.Snapshot(Other);
        Assert.Equal(("other one", "other history"), (other.Feed?.NowPlaying?.Title, other.StationState?.History?[0].Title));
        Assert.Same(NowPlayingSnapshot.Nothing, state.Snapshot(OriginB));
    }

    [Fact]
    public void Retain_drops_the_stations_no_longer_polled()
    {
        var state = new NowPlayingState();
        state.Update(OriginA, Feed("live one"), DateTime.UtcNow);
        state.Update(Other, Feed("other one"), DateTime.UtcNow);

        state.Retain(new HashSet<string> { "other" });

        Assert.Same(NowPlayingSnapshot.Nothing, state.Snapshot(OriginA));
        Assert.Equal("other one", state.Snapshot(Other).Feed?.NowPlaying?.Title);
    }
}
