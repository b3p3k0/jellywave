using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L8: the station fields behind the header for every station (genre, notes, website), their defaults, the limits the
/// server enforces where it reads them, and the website rule (a link only: http or https, no userinfo, at most 2048
/// characters; never requested by the server).
/// </summary>
public class StationHeaderFieldsTests
{
    private static PluginConfiguration Load(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var reader = XmlReader.Create(stream);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }

    private static string Save(PluginConfiguration config)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void A_new_station_has_empty_header_fields()
    {
        var station = new StationDefinition();

        Assert.Equal((string.Empty, string.Empty, string.Empty), (station.Genre, station.Notes, station.Website));
        Assert.Null(StationText.Genre(station));
        Assert.Null(StationText.Notes(station));
        Assert.Null(StationText.Website(station));
    }

    [Fact]
    public void A_file_saved_before_card_l8_loads_with_empty_header_fields()
    {
        // A 0.5.0.0 file written before the fields existed: no Genre, Notes or Website element.
        const string Xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
            + "<SchemaVersion>1</SchemaVersion><Stations><StationDefinition><Id>plain</Id><Name>Plain</Name><Kind>Stream</Kind><Enabled>true</Enabled>"
            + "<StreamUrl>https://cdn.example/a.mp3</StreamUrl></StationDefinition></Stations></PluginConfiguration>";

        var station = Assert.Single(Load(Xml).Stations);

        Assert.Equal((string.Empty, string.Empty, string.Empty), (station.Genre, station.Notes, station.Website));
    }

    [Fact]
    public void The_header_fields_round_trip_through_the_xml_serializer()
    {
        var config = new PluginConfiguration
        {
            SchemaVersion = 1,
            Stations = { new StationDefinition { Id = "plain", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3", Genre = "Jazz & <Blues>", Notes = "Line one\nline two", Website = "https://site.example/?a=1&b=2" } }
        };

        var copy = Assert.Single(Load(Save(config)).Stations);

        Assert.Equal(("Jazz & <Blues>", "https://site.example/?a=1&b=2"), (copy.Genre, copy.Website));
        Assert.Equal("Line one\nline two", copy.Notes.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_migrated_station_has_empty_header_fields()
    {
        var config = new PluginConfiguration { StationBaseUrl = "http://radio.example:7700" };

        ConfigMigration.Migrate(config, _ => Guid.NewGuid());

        var station = Assert.Single(config.Stations);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (station.Genre, station.Notes, station.Website));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   \t\n ", null)]
    [InlineData("  Jazz \t and\r\n\u0001Blues  ", "Jazz and Blues")]
    [InlineData("<b>bold</b>", "<b>bold</b>")]   // markup stays text; the page writes it with textContent
    public void Clean_makes_one_line_of_plain_text(string? value, string? expected)
        => Assert.Equal(expected, StationText.Clean(value, StationDefinition.MaxNotesLength));

    [Fact]
    public void The_limits_cut_with_an_ellipsis_and_keep_text_at_the_limit_whole()
    {
        var exact = new string('g', StationDefinition.MaxGenreLength);
        Assert.Equal(exact, StationText.Genre(new StationDefinition { Genre = exact }));

        var genre = StationText.Genre(new StationDefinition { Genre = exact + "x" })!;
        Assert.Equal(StationDefinition.MaxGenreLength, genre.Length);
        Assert.EndsWith("…", genre, StringComparison.Ordinal);

        var notes = StationText.Notes(new StationDefinition { Notes = new string('n', 1000) })!;
        Assert.Equal(StationDefinition.MaxNotesLength, notes.Length);
        Assert.EndsWith("…", notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://station.example", "https://station.example/")]
    [InlineData("  http://station.example/about?x=1#top ", "http://station.example/about?x=1#top")]   // a link: query and fragment allowed
    [InlineData("HTTPS://Site.Example/A B", "https://site.example/A%20B")]   // served escaped (AbsoluteUri)
    [InlineData("https://site.example/\"><script>", "https://site.example/%22%3E%3Cscript%3E")]
    public void The_website_rule_keeps_http_and_https_links(string raw, string expected)
    {
        Assert.Equal(expected, StationUrls.WebsiteLink(raw));
        Assert.Null(StationUrls.WebsiteRefusal(raw));
    }

    [Theory]
    [InlineData("javascript:alert(1)", StationUrls.RefusedScheme)]
    [InlineData("JavaScript:alert(document.cookie)//https://x", StationUrls.RefusedScheme)]
    [InlineData("data:text/html,<script>alert(1)</script>", StationUrls.RefusedScheme)]
    [InlineData("ftp://files.example/", StationUrls.RefusedScheme)]
    [InlineData("file:///etc/passwd", StationUrls.RefusedScheme)]
    [InlineData("https://user:pw@site.example/", StationUrls.RefusedCredentials)]
    [InlineData("https://@site.example/", StationUrls.RefusedCredentials)]
    [InlineData("station.example", StationUrls.RefusedUnparsable)]   // the admin's field needs the scheme; only the probe adds one
    [InlineData("/relative/path", StationUrls.RefusedScheme)]
    public void The_website_rule_refuses_everything_else(string raw, string reason)
    {
        Assert.Null(StationUrls.WebsiteLink(raw));
        Assert.Equal(reason, StationUrls.WebsiteRefusal(raw));
    }

    [Fact]
    public void A_website_over_2048_characters_is_refused_as_typed_or_as_escaped()
    {
        var atLimit = "https://site.example/" + new string('a', StationUrls.MaxWebsiteLength - "https://site.example/".Length);
        Assert.Equal(atLimit, StationUrls.WebsiteLink(atLimit));

        Assert.Equal(StationUrls.RefusedTooLong, StationUrls.WebsiteRefusal(atLimit + "a"));
        Assert.Null(StationUrls.WebsiteLink(atLimit + "a"));

        // 2000 characters as typed, but every space becomes %20 when served.
        var escaped = "https://site.example/" + new string(' ', 1000) + "x";
        Assert.True(escaped.Length <= StationUrls.MaxWebsiteLength);
        Assert.Equal(StationUrls.RefusedTooLong, StationUrls.WebsiteRefusal(escaped));
    }

    [Fact]
    public void The_as_typed_bound_holds_when_serving_would_shorten_the_link()
    {
        // ":443" and the upper-case scheme and host go when served, so the served link is under the limit; the typed one is not.
        var typed = "HTTPS://SITE.EXAMPLE:443/" + new string('a', StationUrls.MaxWebsiteLength - "HTTPS://SITE.EXAMPLE:443/".Length + 1);
        Assert.Equal(StationUrls.MaxWebsiteLength + 1, typed.Length);
        Assert.True(new Uri(typed).AbsoluteUri.Length <= StationUrls.MaxWebsiteLength);

        Assert.Equal(StationUrls.RefusedTooLong, StationUrls.WebsiteRefusal(typed));
        Assert.Null(StationUrls.WebsiteLink(typed));
    }

    [Theory]
    [InlineData("https://\u0430pple.example/")]   // Cyrillic a: a look-alike of apple.example
    [InlineData("https://b\u00fccher.example/path")]
    public void A_unicode_host_is_served_as_punycode(string raw)
    {
        var link = StationUrls.WebsiteLink(raw)!;

        Assert.StartsWith("https://xn--", link, StringComparison.Ordinal);
        Assert.All(link, c => Assert.True(c is > ' ' and < (char)0x7f, "non-ASCII or control character in " + link));
        Assert.Equal(new Uri(raw).IdnHost, new Uri(link).Host);
    }

    [Fact]
    public void A_host_idna_refuses_is_refused()
    {
        const string Raw = "https://a\u2028b.example/";   // a line separator in the host
        Assert.Null(StationUrls.WebsiteLink(Raw));
        Assert.NotNull(StationUrls.WebsiteRefusal(Raw));
    }

    [Fact]
    public void A_character_idna_maps_away_is_gone_from_the_served_host()
    {
        // IDNA drops a zero-width space; the served link names the host it really goes to.
        Assert.Equal("https://ab.example/", StationUrls.WebsiteLink("https://a\u200Bb.example/"));
    }

    [Fact]
    public void Blank_is_no_website_and_no_refusal()
    {
        Assert.Null(StationUrls.WebsiteLink("  "));
        Assert.Null(StationUrls.WebsiteRefusal(null));
    }

    [Theory]
    [InlineData("station.example", "http://station.example/")]   // a bare host gets http://
    [InlineData("www.station.example/about", "http://www.station.example/about")]
    [InlineData("station.example:8000", "http://station.example:8000/")]
    [InlineData("https://station.example", "https://station.example/")]
    [InlineData("javascript:alert(1)", null)]   // "http://javascript:alert(1)": no valid port
    [InlineData("data:text/html,<b>x</b>", null)]
    [InlineData("mailto:dj@station.example", null)]   // "http://mailto:dj@station.example": userinfo
    [InlineData("ftp://station.example/", null)]
    [InlineData("http://user:pw@station.example/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void An_icy_url_without_a_scheme_gets_http_then_the_website_rule(string? icyUrl, string? expected)
        => Assert.Equal(expected, StationUrls.WebsiteFromIcyUrl(icyUrl));
}
