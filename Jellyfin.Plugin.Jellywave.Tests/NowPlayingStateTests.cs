using System;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class NowPlayingStateTests
{
    private static NowPlayingFeed Feed(string? artist, string? title, string? album = null, string? dj = null, string? show = null, bool online = true, string? coverId = null)
        => new()
        {
            NowPlaying = new TrackInfo { Artist = artist, Title = title, Album = album, SubsonicId = coverId },
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
        Assert.Equal(NowPlayingChange.Description, state.Update(Feed("Keith Kouna", "Rideau", "La Duchesse", "Bobby Tran", "The Ville"), DateTime.UtcNow));
        Assert.Equal("Now playing: Keith Kouna – Rideau (La Duchesse) · DJ Bobby Tran · The Ville", state.Description());
        Assert.Equal("Keith Kouna – Rideau", state.TrackLine());
    }

    [Fact]
    public void Describes_the_next_show_when_the_station_announces_a_handover()
    {
        var feed = Feed("A", "B", show: "Early Shift");
        feed.Context = new ContextInfo { ShowHandover = new ShowHandoverInfo { NextShow = new NextShowInfo { Name = "The Ville", Presenter = "Bobby Tran", StartsAt = "eight in the morning" } } };
        var state = new NowPlayingState();
        state.Update(feed, DateTime.UtcNow);
        Assert.Equal("Now playing: A – B · Early Shift · Up next: The Ville with Bobby Tran at eight in the morning", state.Description());

        feed.Context.ShowHandover.NextShow.Presenter = null;
        feed.Context.ShowHandover.NextShow.StartsAt = "  ";
        state.Update(feed, DateTime.UtcNow);
        Assert.Equal("Now playing: A – B · Early Shift · Up next: The Ville", state.Description());
    }

    [Fact]
    public void Same_track_again_is_not_a_change()
    {
        var state = new NowPlayingState();
        Assert.Equal(NowPlayingChange.Description, state.Update(Feed("A", "B"), DateTime.UtcNow));
        Assert.Equal(NowPlayingChange.None, state.Update(Feed("A", "B"), DateTime.UtcNow));
        Assert.Equal(NowPlayingChange.Description, state.Update(Feed("A", "C"), DateTime.UtcNow));
    }

    [Fact]
    public void Cover_change_is_reported_separately_from_text()
    {
        var state = new NowPlayingState();
        Assert.Equal(NowPlayingChange.Description | NowPlayingChange.Cover, state.Update(Feed("A", "B", coverId: "id-1"), DateTime.UtcNow));
        Assert.Equal("id-1", state.CoverId());
        Assert.Equal(NowPlayingChange.Cover, state.Update(Feed("A", "B", coverId: "id-2"), DateTime.UtcNow));
        Assert.Equal(NowPlayingChange.Description, state.Update(Feed("A", "C", coverId: "id-2"), DateTime.UtcNow));
        Assert.Equal(NowPlayingChange.Cover, state.Update(Feed("A", "C"), DateTime.UtcNow));
        Assert.Null(state.CoverId());
    }

    [Fact]
    public void Off_air_hides_the_cover_and_the_track_line()
    {
        var state = new NowPlayingState();
        state.Update(Feed("A", "B", online: false, coverId: "id-1"), DateTime.UtcNow);
        Assert.Null(state.CoverId());
        Assert.Null(state.TrackLine());
    }

    [Theory]
    [InlineData("tFfm0RjKw9MsPNR8M7i4VB", true)]
    [InlineData("a-b_C9", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../etc", false)]
    [InlineData("a b", false)]
    [InlineData("id?x=1", false)]
    [InlineData("é", false)]
    public void Cover_ids_must_be_safe_path_segments(string? id, bool valid)
    {
        Assert.Equal(valid, StationClient.IsValidId(id));
        var state = new NowPlayingState();
        state.Update(Feed("A", "B", coverId: id), DateTime.UtcNow);
        Assert.Equal(valid ? id : null, state.CoverId());
    }

    [Fact]
    public void Cover_ids_longer_than_64_characters_are_rejected()
    {
        Assert.True(StationClient.IsValidId(new string('a', 64)));
        Assert.False(StationClient.IsValidId(new string('a', 65)));
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

public class NowPlayingStateSanitisingTests
{
    [Fact]
    public void Off_air_wins_over_a_stale_track()
    {
        var state = new NowPlayingState();
        state.Update(new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "A", Title = "B" }, StreamOnline = false }, DateTime.UtcNow);
        Assert.Equal("Off air", state.Description());
    }

    [Fact]
    public void Whitespace_only_fields_count_as_missing()
    {
        var state = new NowPlayingState();
        state.Update(new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "  ", Title = " \n\t " }, Dj = new DjInfo { Name = " " }, StreamOnline = true }, DateTime.UtcNow);
        Assert.Null(state.Description());
    }

    [Fact]
    public void Line_breaks_and_runs_of_spaces_collapse()
    {
        var state = new NowPlayingState();
        state.Update(new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "The\r\n  Band", Title = "A   Song\n" }, StreamOnline = true }, DateTime.UtcNow);
        Assert.Equal("Now playing: The Band – A Song", state.Description());
    }

    [Fact]
    public void Overlong_fields_are_capped()
    {
        var state = new NowPlayingState();
        state.Update(new NowPlayingFeed { NowPlaying = new TrackInfo { Title = new string('x', 10_000) }, StreamOnline = true }, DateTime.UtcNow);
        var text = state.Description()!;
        Assert.Equal("Now playing: " + new string('x', NowPlayingState.MaxFieldLength - 1) + "…", text);
    }
}
