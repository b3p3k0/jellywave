using System;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L1: the whole-URL rule for plain streams (<c>StationKind.Stream</c>, used from L2 on). Absolute http(s) with a
/// host; no userinfo, not even an empty <c>@</c>; no fragment, not even a bare <c>#</c>; a query is allowed (HI decision).
/// The refusal reason never repeats the value.
/// </summary>
public class StreamUrlRuleTests
{
    [Theory]
    [InlineData("https://ice1.somafm.com/groovesalad-128-mp3")]
    [InlineData("http://stream.radioparadise.com/mp3-192")]
    [InlineData("http://radio.example:8000/stream.mp3")]
    [InlineData("https://radio.example/live.mp3?listener=abc&x=1")]
    [InlineData("https://radio.example/live.mp3?")]
    [InlineData("  https://radio.example/live.mp3  ")]
    [InlineData("HTTPS://Radio.Example/Live.mp3")]
    [InlineData("http://[2001:db8::1]:8000/stream")]
    public void Usable_stream_urls_pass(string raw)
    {
        Assert.True(StationUrls.TryParseStreamUrl(raw, out var url));
        Assert.NotNull(url);
        Assert.Null(StationUrls.StreamUrlRefusal(raw));
    }

    [Fact]
    public void The_query_is_kept()
    {
        Assert.True(StationUrls.TryParseStreamUrl("https://radio.example/fip.m3u8?id=radiofrance", out var url));
        Assert.Equal("?id=radiofrance", url!.Query);
    }

    [Theory]
    [InlineData("https://user:secret@radio.example/live.mp3", StationUrls.RefusedCredentials)]
    [InlineData("https://user@radio.example/live.mp3", StationUrls.RefusedCredentials)]
    [InlineData("https://@radio.example/live.mp3", StationUrls.RefusedCredentials)]
    [InlineData("https://:@radio.example/live.mp3", StationUrls.RefusedCredentials)]
    [InlineData("https://radio.example/live.mp3#t=10", StationUrls.RefusedFragment)]
    [InlineData("https://radio.example/live.mp3#", StationUrls.RefusedFragment)]
    [InlineData("https://radio.example/live.mp3?x=1#", StationUrls.RefusedFragment)]
    [InlineData("ftp://radio.example/live.mp3", StationUrls.RefusedScheme)]
    [InlineData("file:///etc/passwd", StationUrls.RefusedScheme)]
    [InlineData("rtsp://radio.example/live", StationUrls.RefusedScheme)]
    [InlineData("radio.example/live.mp3", StationUrls.RefusedUnparsable)]
    [InlineData("http://", StationUrls.RefusedUnparsable)]
    public void Refused_stream_urls_say_why_without_repeating_the_value(string raw, string reason)
    {
        Assert.False(StationUrls.TryParseStreamUrl(raw, out var url));
        Assert.Null(url);
        var refusal = StationUrls.StreamUrlRefusal(raw);
        Assert.Equal(reason, refusal);
        Assert.DoesNotContain("secret", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("radio.example", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rooted_path_is_refused()
    {
        // On Linux Uri reads it as a file: URI (scheme refusal), elsewhere it does not parse; refused either way.
        Assert.False(StationUrls.TryParseStreamUrl("/stream.mp3", out _));
        Assert.Contains(StationUrls.StreamUrlRefusal("/stream.mp3"), new[] { StationUrls.RefusedScheme, StationUrls.RefusedUnparsable });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_not_configured_rather_than_refused(string? raw)
    {
        Assert.False(StationUrls.TryParseStreamUrl(raw, out var url));
        Assert.Null(url);
        Assert.Null(StationUrls.StreamUrlRefusal(raw));
    }

    [Fact]
    public void The_base_url_rules_for_sub_wave_stations_are_unchanged()
    {
        // The plain-stream rule allows a query; the SUB/WAVE base URL rule still refuses it (card 14).
        Assert.True(StationUrls.TryParseStreamUrl("http://radio.example:7700/?x=1", out _));
        Assert.Equal(StationUrls.RefusedQuery, StationUrls.BaseUrlRefusal("http://radio.example:7700/?x=1"));
    }
}
