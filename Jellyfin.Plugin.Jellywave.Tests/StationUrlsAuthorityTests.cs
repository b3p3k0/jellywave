using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14: the base URL carries no credentials, query or fragment; the server stream URL stays on the base's
/// scheme, host, port and userinfo; the client stream URL carries no credentials but may name any host.
/// </summary>
public class StationUrlsAuthorityTests
{
    private static PluginConfiguration Config(string baseUrl, string streamPath = "/stream.mp3", string clientUrl = "")
        => new() { StationBaseUrl = baseUrl, StreamPath = streamPath, ClientStreamUrl = clientUrl };

    [Theory]
    [InlineData("http://user:secret@radio.example:7700")]
    [InlineData("http://user@radio.example")]
    [InlineData("http://@radio.example/")]
    [InlineData("http://radio.example/radio?x=1")]
    [InlineData("http://radio.example/radio?")]
    [InlineData("http://radio.example/#f")]
    [InlineData("http://radio.example/radio#")]
    public void BaseUrl_rejects_userinfo_query_and_fragment(string raw)
    {
        Assert.False(StationUrls.TryGetBaseUrl(Config(raw), out var url));
        Assert.Null(url);
        Assert.False(StationUrls.TryParseBaseUrl(raw, out _));
        Assert.False(StationUrls.TryGetServerStreamUrl(Config(raw), out _));
    }

    [Theory]
    [InlineData("http://radio.example:7700", "http://radio.example:8000/stream.mp3")]   // same host, another port
    [InlineData("http://radio.example:7700", "https://radio.example:7700/stream.mp3")]  // another scheme
    [InlineData("http://radio.example:7700", "http://user@radio.example:7700/stream.mp3")]
    [InlineData("http://radio.example/prefix", "http://radio.example/prefix/stream.mp3")]   // absolute, even on the base itself
    [InlineData("http://radio.example", "live:8000/stream.mp3")]
    [InlineData("http://radio.example:7700", "//radio.example:8000/stream.mp3")]   // "radio.example:" reads as a scheme once trimmed
    [InlineData("http://radio.example:7700", "\\/radio.example:8000/stream.mp3")]   // backslash network path, same host, another port
    public void ServerStreamUrl_rejects_an_absolute_stream_path(string baseUrl, string path)
    {
        Assert.False(StationUrls.TryGetServerStreamUrl(Config(baseUrl, path), out var url));
        Assert.Null(url);
    }

    [Theory]
    [InlineData("http://radio.example:7700", "//live/stream.mp3", "http://radio.example:7700/live/stream.mp3")]
    [InlineData("http://radio.example:80", "/stream.mp3", "http://radio.example/stream.mp3")]   // the default port is the same port
    public void ServerStreamUrl_keeps_a_network_path_under_the_base(string baseUrl, string path, string expected)
    {
        Assert.True(StationUrls.TryGetServerStreamUrl(Config(baseUrl, path), out var url));
        Assert.Equal(expected, url!.ToString());
    }

    [Theory]
    [InlineData("http://listener:secret@public.example/stream.mp3")]
    [InlineData("https://token@public.example/stream.mp3")]
    public void ClientStreamUrl_rejects_userinfo_and_counts_as_set_but_invalid(string raw)
    {
        var config = Config("http://radio.example", clientUrl: raw);
        Assert.True(StationUrls.HasClientStreamUrl(config));
        Assert.False(StationUrls.TryGetClientStreamUrl(config, out var url));
        Assert.Null(url);
    }

    [Fact]
    public void ClientStreamUrl_may_name_another_host_and_port()
    {
        var config = Config("http://radio.example:7700", clientUrl: "http://public.example:8443/stream.mp3");
        Assert.True(StationUrls.TryGetClientStreamUrl(config, out var url));
        Assert.Equal("http://public.example:8443/stream.mp3", url!.ToString());
    }

    [Fact]
    public void Plain_http_stays_allowed()
    {
        Assert.True(StationUrls.TryGetServerStreamUrl(Config("http://radio.example:7700"), out var url));
        Assert.Equal("http://radio.example:7700/stream.mp3", url!.ToString());
    }
}
