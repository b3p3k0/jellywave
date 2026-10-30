using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L2: the tile lists one item per enabled station, in <see cref="StationDefinition.SortOrder"/>, with a media source
/// built from the station; a station that fails its rules is left out without affecting the others.
/// </summary>
public class ChannelItemsTests
{
    private sealed class RecordingLogger : ILogger<JellywaveChannel>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public List<string> Warnings() => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
    }

    private static StationDefinition SubWave(string id, int sort = 0, string host = "radio.example:7700")
        => new() { Id = id, Name = id, Kind = StationKind.SubWave, SortOrder = sort, BaseUrl = "http://" + host, StreamPath = "/stream.mp3" };

    private static StationDefinition Stream(string id, string url, int sort = 0)
        => new() { Id = id, Name = id, Kind = StationKind.Stream, SortOrder = sort, StreamUrl = url };

    private static (JellywaveChannel Channel, RecordingLogger Log, NowPlayingState State) Build(PluginConfiguration config)
    {
        var log = new RecordingLogger();
        var state = new NowPlayingState();
        return (new JellywaveChannel(log, state, null!, new RenameBridge(_ => Guid.Empty), () => config), log, state);
    }

    private static List<ChannelItemInfo> Items(JellywaveChannel channel)
        => channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None).GetAwaiter().GetResult().Items.ToList();

    private static MediaSourceInfo Source(ChannelItemInfo item) => Assert.Single(item.MediaSources);

    [Fact]
    public void N_stations_give_N_items_in_sort_order()
    {
        var config = new PluginConfiguration
        {
            Stations =
            {
                Stream("c", "https://c.example/live.mp3", sort: 3),
                SubWave("a", sort: 1),
                Stream("b", "https://b.example/live.mp3", sort: 2),
                SubWave("d", sort: 3, host: "d.example"),   // tie with "c": list order decides
            }
        };

        var items = Items(Build(config).Channel);

        Assert.Equal(new[] { "a", "b", "c", "d" }, items.Select(i => i.Id));
        Assert.Equal(new[] { "a — Live", "b — Live", "c — Live", "d — Live" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.IsLiveStream));
    }

    [Fact]
    public void One_station_still_gives_one_item_with_the_migrated_id()
    {
        var config = new PluginConfiguration { Stations = { SubWave(ConfigMigration.LegacyMigratedStationId) } };

        var item = Assert.Single(Items(Build(config).Channel));

        Assert.Equal("live", item.Id);
    }

    [Fact]
    public void Disabled_and_invalid_stations_are_skipped_and_the_others_listed()
    {
        var disabled = Stream("off", "https://off.example/live.mp3");
        disabled.Enabled = false;
        var config = new PluginConfiguration
        {
            Stations =
            {
                SubWave("first", sort: 0),
                disabled,
                Stream("creds", "https://user:secret@bad.example/live.mp3", sort: 1),
                new StationDefinition { Id = "subwave-bad", Name = "Bad SUB/WAVE", BaseUrl = "http://bad.example/?q=1", SortOrder = 2 },
                Stream("fragment", "https://bad.example/live.mp3#x", sort: 3),
                Stream("pls", "https://bad.example/listen.pls", sort: 4),
                new StationDefinition { Id = "bad id!", Name = "Bad id", Kind = StationKind.Stream, StreamUrl = "https://ok.example/a.mp3", SortOrder = 5 },
                new StationDefinition { Id = "kind", Name = "Kind", Kind = (StationKind)7, StreamUrl = "https://ok.example/a.mp3", SortOrder = 6 },
                new StationDefinition { Id = "ogg", Name = "Ogg", Kind = StationKind.Stream, StreamUrl = "https://ok.example/a.ogg", Codec = "vorbis", SortOrder = 7 },
                new StationDefinition { Id = "flac", Name = "Flac", Kind = StationKind.Stream, StreamUrl = "https://ok.example/a", Container = "flac", SortOrder = 8 },
                null!,
                Stream("last", "https://last.example/live.mp3", sort: 9),
            }
        };
        var (channel, log, _) = Build(config);

        var items = Items(channel);

        Assert.Equal(new[] { "first", "last" }, items.Select(i => i.Id));
        var warnings = log.Warnings();
        Assert.Equal(8, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("creds", StringComparison.Ordinal) && w.Contains(StationUrls.RefusedCredentials, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Bad SUB/WAVE", StringComparison.Ordinal) && w.Contains(StationUrls.RefusedQuery, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("pls", StringComparison.Ordinal) && w.Contains(StationSources.RefusedPlaylist, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Bad id", StringComparison.Ordinal) && w.Contains(StationSources.RefusedId, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(StationSources.RefusedKind, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(StationSources.RefusedCodec, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(StationSources.RefusedContainer, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("off", StringComparison.Ordinal));

        // A warning names the station, never its URL.
        Assert.DoesNotContain(warnings, w => w.Contains("secret", StringComparison.Ordinal) || w.Contains("bad.example", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_station_warns_once_per_streak()
    {
        var bad = Stream("bad", "ftp://bad.example/live.mp3");
        var config = new PluginConfiguration { Stations = { SubWave("good"), bad } };
        var (channel, log, _) = Build(config);

        Items(channel);
        Items(channel);
        Assert.Single(log.Warnings());

        bad.StreamUrl = "https://bad.example/live.mp3";   // fixed: the streak ends
        Assert.Equal(2, Items(channel).Count);
        bad.StreamUrl = "ftp://bad.example/live.mp3";     // broken again: a new streak
        Items(channel);

        Assert.Equal(2, log.Warnings().Count);
    }

    [Theory]
    [InlineData("https://bad.example/listen.PLS", StationSources.RefusedPlaylist)]
    [InlineData("https://bad.example/listen.m3u", StationSources.RefusedPlaylist)]
    [InlineData("https://bad.example/listen.M3U?x=1", StationSources.RefusedPlaylist)]
    [InlineData("https://bad.example/live.mpd", StationSources.RefusedDash)]
    [InlineData("https://bad.example/LIVE.MPD?token=1", StationSources.RefusedDash)]
    public void Playlist_and_dash_addresses_are_refused_whatever_their_case(string url, string reason)
    {
        var (channel, log, _) = Build(new PluginConfiguration { Stations = { Stream("p", url) } });

        Assert.Empty(Items(channel));
        Assert.Contains(log.Warnings(), w => w.Contains(reason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://ok.example/x.mp3?f=.pls")]
    [InlineData("https://ok.example/x.mp3?f=.m3u")]
    [InlineData("https://ok.example/x.mp3?f=.mpd")]
    public void A_playlist_suffix_only_in_the_query_is_allowed(string url)
    {
        var item = Assert.Single(Items(Build(new PluginConfiguration { Stations = { Stream("ok", url) } }).Channel));

        Assert.Equal(url, Source(item).Path);
    }

    [Fact]
    public void An_earlier_refused_station_does_not_block_a_later_valid_one_with_the_same_id()
    {
        var config = new PluginConfiguration
        {
            Stations = { Stream("dup", "ftp://bad.example/a.mp3", sort: 0), Stream("dup", "https://good.example/a.mp3", sort: 1) }
        };

        var item = Assert.Single(Items(Build(config).Channel));

        Assert.Equal("https://good.example/a.mp3", Source(item).Path);
    }

    [Fact]
    public void When_every_station_is_refused_the_warning_says_so_not_that_none_is_enabled()
    {
        var (channel, log, _) = Build(new PluginConfiguration { Stations = { Stream("a", "ftp://bad.example/a.mp3"), Stream("b", "https://bad.example/b.pls", sort: 1) } });

        Assert.Empty(Items(channel));
        Items(channel);

        var warnings = log.Warnings();
        Assert.Equal(3, warnings.Count);
        Assert.Single(warnings, w => w.Contains("Every enabled station was refused", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("No station is enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_id_is_refused_ignoring_case_and_the_first_one_kept()
    {
        var config = new PluginConfiguration
        {
            Stations = { Stream("Same", "https://one.example/a.mp3", sort: 1), Stream("same", "https://two.example/a.mp3", sort: 2) }
        };
        var (channel, log, _) = Build(config);

        var item = Assert.Single(Items(channel));

        Assert.Equal("https://one.example/a.mp3", Source(item).Path);
        Assert.Contains(log.Warnings(), w => w.Contains(StationSources.RefusedDuplicateId, StringComparison.Ordinal));
    }

    [Fact]
    public void No_station_lists_nothing_and_warns_once()
    {
        var (channel, log, _) = Build(new PluginConfiguration());

        Assert.Empty(Items(channel));
        Assert.Empty(Items(channel));

        Assert.Single(log.Warnings());
    }

    [Fact]
    public void A_subwave_station_in_relay_reads_base_plus_path()
    {
        var item = Assert.Single(Items(Build(new PluginConfiguration { Stations = { SubWave("live") } }).Channel));
        var source = Source(item);

        Assert.Equal("http://radio.example:7700/stream.mp3", source.Path);
        Assert.Null(source.EncoderPath);
        Assert.Null(source.EncoderProtocol);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.False(source.SupportsProbing);
        Assert.True(source.IsInfiniteStream);
        Assert.True(source.IsRemote);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.NotNull(source.RunTimeTicks);
        Assert.Equal("mp3", source.Container);
        var stream = Assert.Single(source.MediaStreams);
        Assert.Equal("mp3", stream.Codec);
        Assert.Equal(192_000, stream.BitRate);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
    }

    /// <summary>Card L10: every kind and format is relayed; nothing offers a client direct play or a second URL.</summary>
    public static TheoryData<string, string, string, string> EveryKindAndFormat() => new()
    {
        { "SubWave", "http://radio.example:7700", "mp3", "http://radio.example:7700/stream.mp3" },
        { "SubWave", "http://radio.example:7700", "aac", "http://radio.example:7700/stream.mp3" },
        { "Stream", "https://cdn.example/live.mp3?token=abc", "mp3", "https://cdn.example/live.mp3?token=abc" },
        { "Stream", "https://cdn.example/live.aac", "aac", "https://cdn.example/live.aac" },
        { "Stream", "https://hls.example/fip.m3u8?id=1", "aac", "https://hls.example/fip.m3u8?id=1" },
    };

    [Theory]
    [MemberData(nameof(EveryKindAndFormat))]
    public void No_station_is_ever_offered_for_direct_play(string kind, string url, string codec, string path)
    {
        var station = kind == "SubWave" ? SubWave("s") : Stream("s", url);
        station.Codec = codec;
        station.Container = codec;

        var source = Source(Assert.Single(Items(Build(new PluginConfiguration { Stations = { station } }).Channel)));

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal(path, source.Path);   // the station URL, which ffmpeg reads (and PlaybackInfo shows, see THREAT-MODEL)
        Assert.Null(source.EncoderPath);
        Assert.Null(source.EncoderProtocol);
    }

    [Fact]
    public void The_station_and_its_source_carry_no_delivery_or_client_url()
    {
        // Card L10: the knobs are gone, so no configuration can bring direct play back.
        string[] gone = ["Delivery", "ClientStreamUrl", "ClientUrl", "Direct"];
        foreach (var type in new[] { typeof(StationDefinition), typeof(PluginConfiguration), typeof(StationSource) })
        {
            Assert.All(gone, name => Assert.Null(type.GetProperty(name)));
        }

        Assert.Null(typeof(PluginConfiguration).Assembly.GetType("Jellyfin.Plugin.Jellywave.Configuration.DeliveryMode"));
    }

    [Fact]
    public void A_plain_stream_in_relay_keeps_its_query()
    {
        var item = Assert.Single(Items(Build(new PluginConfiguration { Stations = { Stream("s", "https://cdn.example/live.mp3?token=abc&x=1") } }).Channel));
        var source = Source(item);

        Assert.Equal("https://cdn.example/live.mp3?token=abc&x=1", source.Path);
        Assert.Null(source.EncoderPath);
        Assert.False(source.SupportsDirectPlay);
    }

    [Fact]
    public void The_format_comes_from_the_station()
    {
        var station = Stream("aac", "https://cdn.example/live");
        station.Codec = " AAC ";
        station.Container = string.Empty;   // blank container follows the codec
        station.BitrateKbps = 128;
        station.SampleRate = 48000;
        station.Channels = 1;

        var source = Source(Assert.Single(Items(Build(new PluginConfiguration { Stations = { station } }).Channel)));

        Assert.Equal("aac", source.Container);
        Assert.False(source.SupportsDirectPlay);
        var stream = Assert.Single(source.MediaStreams);
        Assert.Equal("aac", stream.Codec);
        Assert.Equal(128_000, stream.BitRate);
        Assert.Equal(128_000, source.Bitrate);
        Assert.Equal(48000, stream.SampleRate);
        Assert.Equal(1, stream.Channels);
    }

    [Fact]
    public void A_blank_or_zero_format_falls_back_to_mp3_192_44100_stereo()
    {
        var station = Stream("s", "https://cdn.example/live");
        station.Codec = string.Empty;
        station.Container = " ";
        station.BitrateKbps = 0;
        station.SampleRate = -1;
        station.Channels = 0;

        var source = Source(Assert.Single(Items(Build(new PluginConfiguration { Stations = { station } }).Channel)));

        Assert.Equal("mp3", source.Container);
        var stream = Assert.Single(source.MediaStreams);
        Assert.Equal("mp3", stream.Codec);
        Assert.Equal(192_000, stream.BitRate);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
    }

    [Theory]
    [InlineData("https://hls.example/fip.m3u8?id=radiofrance", "")]
    [InlineData("https://hls.example/FIP.M3U8", "mp3")]
    [InlineData("https://hls.example/live", "hls")]
    [InlineData("https://hls.example/live", "M3U8")]
    public void An_hls_station_is_served_in_relay_with_the_hls_container(string url, string container)
    {
        var station = Stream("hls", url);
        station.Codec = "aac";
        station.Container = container;

        var source = Source(Assert.Single(Items(Build(new PluginConfiguration { Stations = { station } }).Channel)));

        Assert.Equal(StationSources.HlsContainer, source.Container);
        Assert.False(source.SupportsDirectPlay);
        Assert.Null(source.EncoderPath);
        Assert.Equal(new Uri(url).AbsoluteUri, source.Path);
    }

    [Fact]
    public void A_quote_or_backslash_in_the_url_never_reaches_the_media_source_unescaped()
    {
        // Jellyfin 12.3 quotes a remote ffmpeg input without escaping it; Uri.ToString() would turn %22 back into a quote.
        var station = Stream("q", "https://cdn.example/live.mp3?a=%22x%22&b=\"y\"&c=\\z");

        var source = Source(Assert.Single(Items(Build(new PluginConfiguration { Stations = { station } }).Channel)));

        Assert.Null(source.EncoderPath);   // ffmpeg reads Path itself
        Assert.DoesNotContain("\"", source.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", source.Path, StringComparison.Ordinal);
        Assert.Contains("a=%22x%22", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sub_wave_item_carries_its_own_now_playing_line()
    {
        // Card L4: the line is keyed by station id and origin; a stream and a station not heard yet get the default text.
        var config = new PluginConfiguration
        {
            Stations =
            {
                Stream("plain", "https://cdn.example/a.mp3", sort: 0),
                SubWave("one", sort: 1, host: "one.example"),
                SubWave("two", sort: 2, host: "two.example"),
                SubWave("three", sort: 3, host: "three.example"),
            }
        };
        var (channel, _, state) = Build(config);
        state.Update(new StationKey("one", "http://one.example/"), new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "A", Title = "One" }, StreamOnline = true }, DateTime.UtcNow);
        state.Update(new StationKey("two", "http://two.example/"), new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "B", Title = "Two" }, StreamOnline = true }, DateTime.UtcNow);
        state.Update(new StationKey("three", "http://elsewhere.example/"), new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "C", Title = "Old origin" }, StreamOnline = true }, DateTime.UtcNow);

        var items = Items(channel);

        Assert.Equal(JellywaveChannel.DefaultOverview, items[0].Overview);
        Assert.StartsWith("Now playing: A – One", items[1].Overview, StringComparison.Ordinal);
        Assert.StartsWith("Now playing: B – Two", items[2].Overview, StringComparison.Ordinal);
        Assert.Equal(JellywaveChannel.DefaultOverview, items[3].Overview);
    }

    [Fact]
    public void A_plain_stream_s_overview_is_its_notes_and_a_sub_wave_station_s_stays_the_now_playing_text()
    {
        // Card L8: notes on the item, so every client shows them in Jellyfin's own detail view.
        var plain = Stream("plain", "https://cdn.example/a.mp3", sort: 0);
        plain.Notes = "  Listener-supported,\n90.3 FM. ";
        var bare = Stream("bare", "https://cdn.example/b.mp3", sort: 1);
        var sub = SubWave("one", sort: 2, host: "one.example");
        sub.Notes = "Station notes";
        var config = new PluginConfiguration { Stations = { plain, bare, sub } };

        var items = Items(Build(config).Channel);

        Assert.Equal("Listener-supported, 90.3 FM.", items[0].Overview);
        Assert.Equal(JellywaveChannel.DefaultOverview, items[1].Overview);
        Assert.Equal(JellywaveChannel.DefaultOverview, items[2].Overview);   // not heard yet: the now-playing default, not the notes
    }

    [Fact]
    public void A_plain_stream_s_overview_drops_angle_brackets()
    {
        // Card L8 fix round: notes may come from a third-party icy-description, and jellyfin-web renders an overview as
        // sanitized HTML and Markdown; no tag reaches it. The station page shows the notes as they are, as text.
        var plain = Stream("plain", "https://cdn.example/a.mp3");
        plain.Notes = "<script>alert(1)</script> Live <b>now</b> <img src=x onerror=alert(1)>";
        var only = Stream("only", "https://cdn.example/b.mp3", sort: 1);
        only.Notes = "<<>>";
        var config = new PluginConfiguration { Stations = { plain, only } };

        var items = Items(Build(config).Channel);

        Assert.Equal("scriptalert(1)/script Live bnow/b img src=x onerror=alert(1)", items[0].Overview);
        Assert.Equal(JellywaveChannel.DefaultOverview, items[1].Overview);   // nothing left: the default text
    }

    [Fact]
    public void The_item_date_carries_the_l10_media_source_schema_date()
    {
        // Jellyfin re-saves a channel item's media sources only when DateModified advances; L2 and L10 (Relay only, so a
        // station saved in Direct loses its direct-play source on upgrade) changed their shape.
        var item = Assert.Single(Items(Build(new PluginConfiguration { Stations = { SubWave("live") } }).Channel));

        Assert.True(item.DateModified >= new DateTime(2026, 10, 28, 19, 0, 0, DateTimeKind.Utc), "bump MediaSourceSchemaDate with the media source shape");
    }

    [Fact]
    public void No_item_carries_an_image_url()
    {
        // Jellyfin would fetch ImageUrl with its own client, outside the card 14 controls.
        var station = Stream("s", "https://cdn.example/a.mp3");
        station.LogoUrl = "https://cdn.example/logo.png";

        Assert.All(Items(Build(new PluginConfiguration { Stations = { station, SubWave("live", sort: 1) } }).Channel), i => Assert.Null(i.ImageUrl));
    }
}
