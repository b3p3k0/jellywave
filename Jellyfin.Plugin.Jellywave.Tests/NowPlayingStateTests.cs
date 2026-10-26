using System;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class NowPlayingStateTests
{
    private static NowPlayingFeed Feed(string? artist, string? title, string? album = null, string? dj = null, string? show = null, bool online = true)
        => new()
        {
            NowPlaying = new TrackInfo { Artist = artist, Title = title, Album = album },
            Dj = dj is null ? null : new DjInfo { Name = dj },
            ActiveShow = show is null ? null : new ShowInfo { Name = show },
            StreamOnline = online
        };

    [Fact]
    public void Empty_state_has_no_description()
    {
        Assert.Null(new NowPlayingState().Description());
    }

    [Fact]
    public void Describes_track_album_dj_and_show()
    {
        var state = new NowPlayingState();
        Assert.True(state.Update(Feed("Keith Kouna", "Rideau", "La Duchesse", "Bobby Tran", "The Ville"), DateTime.UtcNow));
        Assert.Equal("Now playing: Keith Kouna – Rideau (La Duchesse) · DJ Bobby Tran · The Ville", state.Description());
    }

    [Fact]
    public void Same_track_again_is_not_a_change()
    {
        var state = new NowPlayingState();
        Assert.True(state.Update(Feed("A", "B"), DateTime.UtcNow));
        Assert.False(state.Update(Feed("A", "B"), DateTime.UtcNow));
        Assert.True(state.Update(Feed("A", "C"), DateTime.UtcNow));
    }

    [Fact]
    public void Off_air_without_a_track_says_so()
    {
        var state = new NowPlayingState();
        state.Update(Feed(null, null, online: false, dj: "Bobby"), DateTime.UtcNow);
        Assert.Equal("Off air · DJ Bobby", state.Description());
    }

    [Fact]
    public void Missing_artist_or_album_degrades_gracefully()
    {
        var state = new NowPlayingState();
        state.Update(Feed(null, "Station ident"), DateTime.UtcNow);
        Assert.Equal("Now playing: Station ident", state.Description());
    }

    [Fact]
    public void Clear_forgets_the_feed_and_reports_whether_there_was_one()
    {
        var state = new NowPlayingState();
        Assert.False(state.Clear());
        state.Update(Feed("A", "B"), DateTime.UtcNow);
        Assert.True(state.Clear());
        Assert.Null(state.Description());
    }
}
