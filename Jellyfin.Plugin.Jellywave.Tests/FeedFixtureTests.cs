using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Deserialises feeds shaped like real SUB/WAVE 1.26 responses, so key names are checked in CI
/// and not only on a live station.
/// </summary>
public class FeedFixtureTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Trimmed copy of GET /api/now-playing (values neutralised; unknown keys kept to prove they are ignored).
    private const string NowPlayingJson = """
        {"nowPlaying":{"title":"Rideau","artist":"Keith Kouna","album":"La Duchesse","subsonic_id":"abc123",
          "filename":"/tmp/x.mp3","timestamp":1793041497,"genres":["Indie"],"bpm":117.5,"duration":277},
         "context":{"at":"2026-10-26T19:07:25.084Z","weather":{"condition":"clear"}},
         "activeShow":{"id":"s_1","name":"The Ville","topic":"..."},
         "dj":{"name":"Bobby Tran","tagline":"Your older brother's record collection.","avatar":"/persona-avatar/p1","station":"Example FM"},
         "listeners":{"current":1,"peak":3},
         "stream":{"mount":"/stream.mp3","format":"mp3","bitrate":192,"sampleRate":44100,"channels":2,"bufferSeconds":8},
         "streamBitrate":192,"streamOnline":true,"session":{"id":"sess_1","kind":"show"}}
        """;

    private const string StateJson = """
        {"current":{"title":"x"},"privacy":{"privatePlayer":false,"listenerAuth":true},
         "station":{"id":null,"name":"Example FM","multiStation":false},"needsSetup":false}
        """;

    [Fact]
    public void Now_playing_feed_maps_the_keys_the_plugin_relies_on()
    {
        var feed = JsonSerializer.Deserialize<NowPlayingFeed>(NowPlayingJson, Options)!;
        Assert.Equal("Keith Kouna", feed.NowPlaying!.Artist);
        Assert.Equal("Rideau", feed.NowPlaying.Title);
        Assert.Equal("La Duchesse", feed.NowPlaying.Album);
        Assert.True(feed.StreamOnline);
        Assert.Equal("/stream.mp3", feed.Stream!.Mount);
        Assert.Equal(192, feed.Stream.Bitrate);
        Assert.Equal(44100, feed.Stream.SampleRate);
        Assert.Equal(2, feed.Stream.Channels);
        Assert.Equal("Bobby Tran", feed.Dj!.Name);
        Assert.Equal("Example FM", feed.Dj.Station);
        Assert.Equal("The Ville", feed.ActiveShow!.Name);

        var state = new NowPlayingState();
        state.Update(feed, System.DateTime.UtcNow);
        Assert.Equal("Now playing: Keith Kouna – Rideau (La Duchesse) · DJ Bobby Tran · The Ville", state.Description());
    }

    [Fact]
    public void Missing_online_flag_does_not_hide_the_track()
    {
        var feed = JsonSerializer.Deserialize<NowPlayingFeed>("""{"nowPlaying":{"title":"T","artist":"A"}}""", Options)!;
        Assert.Null(feed.StreamOnline);
        var state = new NowPlayingState();
        state.Update(feed, System.DateTime.UtcNow);
        Assert.Equal("Now playing: A – T", state.Description());
    }

    [Fact]
    public void State_feed_maps_name_and_privacy()
    {
        var state = JsonSerializer.Deserialize<StationState>(StateJson, Options)!;
        Assert.Equal("Example FM", state.Station!.Name);
        Assert.False(state.Privacy!.PrivatePlayer);
        Assert.True(state.Privacy.ListenerAuth);
    }
}
