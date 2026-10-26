using System;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class StationUrlsTests
{
    private static PluginConfiguration Config(string baseUrl, string streamPath = "/stream.mp3", string clientUrl = "")
        => new() { StationBaseUrl = baseUrl, StreamPath = streamPath, ClientStreamUrl = clientUrl };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("radio.example:7700")]
    [InlineData("ftp://radio.example/")]
    [InlineData("file:///etc/passwd")]
    public void BaseUrl_rejects_missing_or_non_http(string raw)
    {
        Assert.False(StationUrls.TryGetBaseUrl(Config(raw), out var url));
        Assert.Null(url);
    }

    [Theory]
    [InlineData("http://radio.example:7700", "http://radio.example:7700/")]
    [InlineData("http://radio.example:7700/", "http://radio.example:7700/")]
    [InlineData("https://radio.example/prefix///", "https://radio.example/prefix/")]
    [InlineData("  http://radio.example  ", "http://radio.example/")]
    public void BaseUrl_normalises_to_trailing_slash(string raw, string expected)
    {
        Assert.True(StationUrls.TryGetBaseUrl(Config(raw), out var url));
        Assert.Equal(expected, url!.ToString());
    }

    [Theory]
    [InlineData("http://radio.example:7700", "/stream.mp3", "http://radio.example:7700/stream.mp3")]
    [InlineData("http://radio.example:7700", "stream.mp3", "http://radio.example:7700/stream.mp3")]
    [InlineData("http://radio.example:7700", "//stream.mp3", "http://radio.example:7700/stream.mp3")]
    [InlineData("http://radio.example/prefix", "/stream.mp3", "http://radio.example/prefix/stream.mp3")]
    [InlineData("http://radio.example/prefix", "live/stream.aac", "http://radio.example/prefix/live/stream.aac")]
    public void ServerStreamUrl_resolves_under_the_base(string baseUrl, string path, string expected)
    {
        Assert.True(StationUrls.TryGetServerStreamUrl(Config(baseUrl, path), out var url));
        Assert.Equal(expected, url!.ToString());
    }

    [Theory]
    [InlineData("http://radio.example/prefix", "../other.mp3")]
    [InlineData("http://radio.example/prefix", "a/../../other.mp3")]
    [InlineData("http://radio.example", "http://evil.example/x.mp3")]
    [InlineData("http://radio.example", "")]
    [InlineData("http://radio.example", "   ")]
    public void ServerStreamUrl_rejects_paths_that_escape_the_base(string baseUrl, string path)
    {
        Assert.False(StationUrls.TryGetServerStreamUrl(Config(baseUrl, path), out var url));
        Assert.Null(url);
    }

    [Fact]
    public void ServerStreamUrl_fails_when_base_is_missing()
    {
        Assert.False(StationUrls.TryGetServerStreamUrl(Config(string.Empty), out _));
    }

    [Fact]
    public void ClientStreamUrl_empty_means_unset()
    {
        var config = Config("http://radio.example", clientUrl: "  ");
        Assert.False(StationUrls.HasClientStreamUrl(config));
        Assert.False(StationUrls.TryGetClientStreamUrl(config, out _));
    }

    [Theory]
    [InlineData("radio.example/stream.mp3")]
    [InlineData("rtsp://radio.example/stream")]
    public void ClientStreamUrl_set_but_invalid_is_reported_as_set_and_invalid(string raw)
    {
        var config = Config("http://radio.example", clientUrl: raw);
        Assert.True(StationUrls.HasClientStreamUrl(config));
        Assert.False(StationUrls.TryGetClientStreamUrl(config, out var url));
        Assert.Null(url);
    }

    [Fact]
    public void ClientStreamUrl_valid_is_returned_verbatim()
    {
        var config = Config("http://radio.example", clientUrl: " https://public.example/stream.mp3 ");
        Assert.True(StationUrls.TryGetClientStreamUrl(config, out var url));
        Assert.Equal("https://public.example/stream.mp3", url!.ToString());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(20, 20)]
    [InlineData(301, 300)]
    public void EffectivePollSeconds_is_clamped(int configured, int expected)
    {
        var config = new PluginConfiguration { NowPlayingPollSeconds = configured };
        Assert.Equal(expected, config.EffectivePollSeconds);
    }
}

public class StationUrlsQueryAndFragmentTests
{
    [Theory]
    [InlineData("stream.mp3?auth=x")]
    [InlineData("stream.mp3#frag")]
    public void Query_and_fragment_are_kept_but_stay_under_the_base(string path)
    {
        var config = new PluginConfiguration { StationBaseUrl = "http://radio.example/prefix", StreamPath = path };
        Assert.True(StationUrls.TryGetServerStreamUrl(config, out var url));
        Assert.StartsWith("http://radio.example/prefix/stream.mp3", url!.ToString(), StringComparison.Ordinal);
    }
}
