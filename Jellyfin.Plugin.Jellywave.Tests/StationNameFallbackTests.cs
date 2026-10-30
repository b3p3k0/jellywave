using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L11 (G1.3, G1.4): a plain stream with a blank name is named after its stream URL's host in ASCII form, the way the
/// configuration page names it, never "SUB/WAVE"; a SUB/WAVE station keeps "SUB/WAVE". The tile's default description no
/// longer names SUB/WAVE.
/// </summary>
public class StationNameFallbackTests
{
    private static StationDefinition Stream(string name, string url) => new() { Id = "s", Name = name, Kind = StationKind.Stream, StreamUrl = url };

    [Theory]
    [InlineData("https://Stream.Example.org:8443/live.mp3?x=1", "stream.example.org")]
    [InlineData("http://bücher.example/x.mp3", "xn--bcher-kva.example")]
    [InlineData("http://[2001:DB8::1]:8000/x.mp3", "[2001:db8::1]")]
    [InlineData("http://192.0.2.7/x.mp3", "192.0.2.7")]
    [InlineData(" https://padded.example/x.mp3 ", "padded.example")]
    public void A_blank_plain_stream_is_named_after_its_host(string url, string expected)
    {
        Assert.Equal(expected, JellywaveChannel.StationName(Stream(string.Empty, url)));
        Assert.Equal(expected, JellywaveChannel.StationName(Stream("   ", url)));
        Assert.Equal(expected + " — Live", JellywaveChannel.ItemName(Stream(string.Empty, url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://files.example/x.mp3")]
    [InlineData("/relative/x.mp3")]
    public void A_blank_plain_stream_without_an_http_host_is_unnamed(string url)
        => Assert.Equal(JellywaveChannel.UnnamedStreamName, JellywaveChannel.StationName(Stream(string.Empty, url)));

    [Fact]
    public void The_unnamed_text_is_plain()
        => Assert.Equal("Unnamed stream", JellywaveChannel.UnnamedStreamName);

    [Fact]
    public void A_blank_sub_wave_station_and_no_station_keep_sub_wave()
    {
        Assert.Equal("SUB/WAVE", JellywaveChannel.StationName(new StationDefinition { Kind = StationKind.SubWave, Name = " ", BaseUrl = "http://radio.example" }));
        Assert.Equal("SUB/WAVE", JellywaveChannel.StationName((StationDefinition?)null));
    }

    [Fact]
    public void A_name_wins_and_is_trimmed()
        => Assert.Equal("Plain FM", JellywaveChannel.StationName(Stream("  Plain FM ", "https://cdn.example/x.mp3")));

    [Fact]
    public void The_sort_name_uses_the_fallback()
    {
        var station = Stream(string.Empty, "https://cdn.example/x.mp3");
        Assert.Equal("0001 cdn.example", NowPlayingService.SortNameOf(1, station));
    }

    [Fact]
    public void The_default_tile_description_names_no_station_kind()
    {
        Assert.Equal("Live internet radio.", JellywaveChannel.DefaultDescription);
        var channel = new JellywaveChannel(
            NullLogger<JellywaveChannel>.Instance,
            new NowPlayingState(),
            null!,
            new RenameBridge(_ => System.Guid.Empty),
            () => new PluginConfiguration { Stations = { Stream("Plain", "https://cdn.example/x.mp3") } });
        Assert.Equal("Live internet radio.", channel.Description);
    }
}
