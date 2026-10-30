using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jellywave.Web;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Cards L5 and L8: the station page script as embedded (THREAT-MODEL boundary 5). Station text reaches the page as text
/// only: the only markup is three fixed strings (the SUB/WAVE header, the plain stream header and the drawer link). Every station route it calls names its
/// station, and it never reads a URL from a reply. The behaviour is tested in <c>js/stationPage.test.js</c>.
/// </summary>
public class StationPageScriptTests
{
    private static string Script()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(WebUiTransforms.ScriptResource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("setHTML")]
    [InlineData("parseHTMLUnsafe")]
    [InlineData("createContextualFragment")]
    [InlineData("parseFromString")]
    [InlineData("srcdoc")]
    [InlineData(".html(")]
    public void The_script_has_no_other_markup_sink(string sink)
        => Assert.DoesNotContain(sink, Script(), StringComparison.Ordinal);

    [Fact]
    public void Inner_html_is_only_ever_a_fixed_string()
    {
        var script = Script();
        var uses = Regex.Matches(script, @"innerHTML\s*=\s*(?<rest>[^;]*);", RegexOptions.Singleline);
        Assert.Equal(3, Regex.Matches(script, "innerHTML", RegexOptions.None).Count);
        Assert.Equal(3, uses.Count);
        foreach (Match use in uses)
        {
            // Only single-quoted literals joined with +: no identifier, call or property in the value.
            var rest = Regex.Replace(use.Groups["rest"].Value, @"'(?:[^'\\]|\\.)*'", string.Empty);
            rest = Regex.Replace(rest, @"//[^\n]*", string.Empty);
            Assert.Matches(@"^[\s+]*$", rest);
        }
    }

    [Fact]
    public void A_website_becomes_an_href_only_after_the_scheme_check()
    {
        // Card L8: the one place an href is set from station data, behind the page's own http/https check.
        var script = Script();
        Assert.Single(Regex.Matches(script, @"setAttribute\('href'"));
        Assert.Contains("link.setAttribute('href', site.href)", script, StringComparison.Ordinal);
        Assert.Contains("var site = safeWebsite(station.Website);", script, StringComparison.Ordinal);
        Assert.Contains(@"var WEBSITE_RULE = /^https?:\/\/", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".href =", script.Replace("link.href = '#'", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_station_route_call_names_the_station()
    {
        var script = Script();
        foreach (var route in new[] { "Jellywave/Feed", "Jellywave/Like", "Jellywave/Lyrics", "Jellywave/Schedule", "Jellywave/Title" })
        {
            var calls = Regex.Matches(script, @"getUrl\('" + route + @"'(?<args>[^)]*)\)").Cast<Match>().ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, c => Assert.True(c.Groups["args"].Value.Contains("stationId", StringComparison.Ordinal) || route == "Jellywave/Like", route + c.Value));
        }

        Assert.Contains("getUrl('Jellywave/Stations')", script, StringComparison.Ordinal);
        Assert.Matches(@"getUrl\('Jellywave/Avatar/' \+ encodeURIComponent\([^)]*\), \{ stationId: ", script);
        Assert.Contains("StationId: stationId", script, StringComparison.Ordinal);   // request body
        Assert.Contains("StationId: state.selected", script, StringComparison.Ordinal);   // like body
        Assert.Matches(@"getUrl\('Jellywave/Like', \{ stationId: state\.selected \}\)", script);
        Assert.Matches(@"getUrl\('Jellywave/Title', \{ stationId: stationId \}\)", script);   // card L6
    }
}
