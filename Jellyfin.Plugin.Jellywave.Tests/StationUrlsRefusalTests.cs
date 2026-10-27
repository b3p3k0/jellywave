using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14 review: a saved base URL that the card 14 rules refuse says why, instead of reading as "not configured".
/// </summary>
public class StationUrlsRefusalTests
{
    [Theory]
    [InlineData("http://radio.example:7700/?", StationUrls.RefusedQuery)]
    [InlineData("http://radio.example/radio?x=1", StationUrls.RefusedQuery)]
    [InlineData("http://radio.example:7700/#", StationUrls.RefusedFragment)]
    [InlineData("http://radio.example/#f", StationUrls.RefusedFragment)]
    [InlineData("http://user:secret@radio.example:7700", StationUrls.RefusedCredentials)]
    [InlineData("http://@radio.example", StationUrls.RefusedCredentials)]
    [InlineData("ftp://radio.example/", StationUrls.RefusedScheme)]
    [InlineData("file:///etc/passwd", StationUrls.RefusedScheme)]
    [InlineData("radio.example:7700", StationUrls.RefusedScheme)]   // parses with "radio.example" as the scheme
    [InlineData("not a url", StationUrls.RefusedUnparsable)]
    public void A_refused_value_says_why(string raw, string reason)
    {
        Assert.Equal(reason, StationUrls.BaseUrlRefusal(raw));
        Assert.False(StationUrls.TryParseBaseUrl(raw, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://radio.example:7700")]
    [InlineData("https://radio.example/prefix/")]
    public void Blank_or_usable_has_no_reason(string? raw)
        => Assert.Null(StationUrls.BaseUrlRefusal(raw));

    [Fact]
    public void The_reason_never_repeats_the_credentials()
    {
        var reason = StationUrls.BaseUrlRefusal("http://listener:hunter2@radio.example");
        Assert.NotNull(reason);
        Assert.DoesNotContain("hunter2", reason, System.StringComparison.Ordinal);
        Assert.DoesNotContain("listener", reason, System.StringComparison.Ordinal);
    }
}
