using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Cards L5 and L8: <c>GET /Jellywave/Stations</c> lists every listed station in grid order for the station page: id, name,
/// kind, the live item id and its image tag (a SUB/WAVE cover withheld under the L4 old-origin rule), the notes, and a plain
/// stream's genre, website and format. Never a stream or station URL, never a disabled, refused or duplicate station.
/// </summary>
public class StationListRouteTests
{
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private static async Task<StationsResponse> ListAsync(MultiStationHarness h)
        => WebRoute.Value<StationsResponse>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Stations)));

    [Fact]
    public async Task Lists_every_listed_station_in_grid_order_with_its_kind()
    {
        var h = new MultiStationHarness();

        var list = await ListAsync(h);

        Assert.Equal(h.Channel.Id, list.ChannelId);
        Assert.Equal("Live Radio", list.ChannelName);
        Assert.Equal(new[] { "live", "two", "plain" }, list.Stations.Select(s => s.Id));
        Assert.Equal(new[] { "SubWave", "SubWave", "Stream" }, list.Stations.Select(s => s.Kind));
        Assert.Equal(new[] { "Live One", "Station <b>Two</b>", "Plain" }, list.Stations.Select(s => s.Name));
        Assert.Equal(new Guid?[] { h.LiveItem.Id, h.TwoItem.Id, h.PlainItem.Id }, list.Stations.Select(s => s.ItemId));
        Assert.All(list.Stations.Take(2), s => Assert.Equal(FeedStationSwitchTests.TagProcessor.Tag, s.ImageTag));
        Assert.Null(list.Stations[2].ImageTag);   // the plain item has no image yet
        Assert.Empty(h.Seen);   // nothing is asked of any station
    }

    [Fact]
    public async Task Follows_a_reorder()
    {
        var h = new MultiStationHarness();
        h.Config.Stations[1].SortOrder = -1;
        h.Config.Stations[2].SortOrder = -2;

        Assert.Equal(new[] { "plain", "two", "live" }, (await ListAsync(h)).Stations.Select(s => s.Id));
    }

    [Fact]
    public async Task Never_carries_a_url()
    {
        var h = new MultiStationHarness();
        var list = await ListAsync(h);

        // Serialized as Jellyfin's default (PascalCase) and as camelCase: neither carries any part of a station URL. The
        // plain stream's kind is the one "Stream" allowed. Card L11: a plain stream with a blank name is named after its
        // stream URL's host, and nothing else of the URL (A_blank_plain_stream_is_named_by_its_host_alone).
        foreach (var json in new[] { JsonSerializer.Serialize(list), JsonSerializer.Serialize(list, CamelCase) })
        {
            var rest = json.Replace("\"Stream\"", string.Empty, StringComparison.Ordinal);

            // A port is banned with its colon: a bare "7700" can turn up in a random item id (fix round 2).
            foreach (var banned in new[] { "http", "://", ".example", ":7700", "/radio", "hidden-mount", "secret-query", "token", "stream", "Url", "p.mp3" })
            {
                Assert.DoesNotContain(banned, rest, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The shape itself: only these fields exist, so a URL field added later fails here.
        Assert.Equal(new[] { "ChannelId", "ChannelName", "Stations" }, typeof(StationsResponse).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(
            new[] { "BitrateKbps", "Codec", "Genre", "Id", "ImageTag", "ItemId", "Kind", "Name", "Notes", "Website" },
            typeof(StationListEntry).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_blank_plain_stream_is_named_by_its_host_alone()
    {
        // Card L11: the name is the host in ASCII form; port, path, query and userinfo never reach the list.
        var h = new MultiStationHarness();
        var plain = h.Config.Stations.Single(s => s.Id == "plain");
        plain.Name = " ";
        plain.StreamUrl = "https://" + MultiStationHarness.HostPlain + ":8443/hidden-mount/p.mp3?token=secret-query";

        var list = await ListAsync(h);

        Assert.Equal(MultiStationHarness.HostPlain, list.Stations.Single(s => s.Id == "plain").Name);
        foreach (var json in new[] { JsonSerializer.Serialize(list), JsonSerializer.Serialize(list, CamelCase) })
        {
            // A port is banned with its colon: a bare "8443" can turn up in a random item id (fix round 2: it once did).
            foreach (var banned in new[] { "http", "://", ":8443", "hidden-mount", "p.mp3", "secret-query", "token" })
            {
                Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task A_plain_stream_carries_its_header_fields_and_a_sub_wave_station_its_notes()
    {
        var h = new MultiStationHarness();
        foreach (var station in h.Config.Stations)
        {
            station.Genre = "Genre " + station.Id;
            station.Notes = "Notes " + station.Id;
            station.Website = "https://site-" + station.Id + ".example/about";
        }

        h.Config.Stations[2].BitrateKbps = 128;
        var list = await ListAsync(h);

        var plain = list.Stations.Single(s => s.Id == "plain");
        Assert.Equal("Genre plain", plain.Genre);
        Assert.Equal("Notes plain", plain.Notes);
        Assert.Equal("https://site-plain.example/about", plain.Website);
        Assert.Equal("MP3", plain.Codec);
        Assert.Equal(128, plain.BitrateKbps);

        var two = list.Stations.Single(s => s.Id == "two");
        Assert.Equal("Notes two", two.Notes);
        Assert.Null(two.Genre);
        Assert.Null(two.Website);
        Assert.Null(two.Codec);
        Assert.Null(two.BitrateKbps);

        // The website is the one link, and the stream URL is still nowhere in the answer.
        var json = JsonSerializer.Serialize(list);
        foreach (var banned in new[] { MultiStationHarness.HostPlain, "secret-query", "p.mp3", MultiStationHarness.HostLive, MultiStationHarness.HostTwo, "hidden-mount" })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("ftp://files.example/")]
    [InlineData("https://user:pw@site.example/")]
    [InlineData("site.example")]
    [InlineData("   ")]
    public async Task A_website_the_rule_refuses_is_dropped_and_the_station_stays_listed(string website)
    {
        var h = new MultiStationHarness();
        h.Config.Stations[2].Website = website;
        h.Config.Stations[2].Genre = "Jazz";

        var plain = (await ListAsync(h)).Stations.Single(s => s.Id == "plain");

        Assert.Null(plain.Website);
        Assert.Equal("Jazz", plain.Genre);
    }

    [Fact]
    public async Task The_limits_are_enforced_where_the_list_is_served()
    {
        // The configuration page is not trusted for its maxlength: a hand-edited file or an API save can hold more.
        var h = new MultiStationHarness();
        h.Config.Stations[2].Genre = new string('g', 100);
        h.Config.Stations[2].Notes = "line one\nline <b>two</b>  " + new string('n', 400);
        h.Config.Stations[0].Notes = new string('s', 300);

        var list = await ListAsync(h);
        var plain = list.Stations.Single(s => s.Id == "plain");

        Assert.Equal(StationDefinition.MaxGenreLength, plain.Genre!.Length);
        Assert.EndsWith("…", plain.Genre, StringComparison.Ordinal);
        Assert.Equal(StationDefinition.MaxNotesLength, plain.Notes!.Length);
        Assert.StartsWith("line one line <b>two</b> nnn", plain.Notes, StringComparison.Ordinal);   // one line, markup kept as text
        Assert.Equal(StationDefinition.MaxNotesLength, list.Stations.Single(s => s.Id == "live").Notes!.Length);
    }

    [Fact]
    public async Task A_plain_stream_s_format_follows_what_is_served()
    {
        var h = new MultiStationHarness();
        var plain = h.Config.Stations[2];
        plain.Codec = "aac";
        plain.Container = "aac";
        plain.BitrateKbps = 0;

        var aac = (await ListAsync(h)).Stations.Single(s => s.Id == "plain");
        Assert.Equal(("AAC", (int?)null), (aac.Codec, aac.BitrateKbps));

        plain.StreamUrl = "https://" + MultiStationHarness.HostPlain + "/live.m3u8";
        var hls = (await ListAsync(h)).Stations.Single(s => s.Id == "plain");
        Assert.Equal("HLS", hls.Codec);
    }

    [Fact]
    public async Task The_answer_names_no_delivery_mode()
    {
        // Card L10: every station is relayed, so the list says nothing about delivery, in any casing.
        var json = JsonSerializer.Serialize(await ListAsync(new MultiStationHarness()));

        foreach (var banned in new[] { "Delivery", "Relay", "Direct" })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_plain_stream_s_logo_has_a_tag()
    {
        var h = new MultiStationHarness();
        h.PlainItem.SetImagePath(ImageType.Primary, "http://saved.example/plain");

        Assert.Equal(FeedStationSwitchTests.TagProcessor.Tag, (await ListAsync(h)).Stations.Single(s => s.Id == "plain").ImageTag);
    }

    [Fact]
    public async Task The_feed_never_carries_a_url_either()
    {
        var h = new MultiStationHarness();
        var feed = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), "two"));

        var json = JsonSerializer.Serialize(feed);
        foreach (var banned in new[] { "http", "://", "station-two.example", ":7700", "hidden-mount" })   // the port with its colon, as above
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Without_a_sub_wave_station_the_list_holds_the_plain_streams()
    {
        var h = new MultiStationHarness();
        foreach (var station in h.Config.Stations.Where(s => s.Kind == StationKind.SubWave).ToList())
        {
            h.Config.Stations.Remove(station);
        }

        var list = await ListAsync(h);

        Assert.Equal(new[] { "plain" }, list.Stations.Select(s => s.Id));
        Assert.Equal(h.Channel.Id, list.ChannelId);
    }

    [Fact]
    public async Task Without_any_listed_station_the_list_is_empty()
    {
        var h = new MultiStationHarness();
        foreach (var station in h.Config.Stations)
        {
            station.Enabled = false;
        }

        Assert.Empty((await ListAsync(h)).Stations);
    }

    [Fact]
    public async Task A_station_whose_item_does_not_exist_yet_has_no_item_id_and_no_tag()
    {
        var h = new MultiStationHarness();
        h.Library.ListedItems.Remove(h.TwoItem);

        var two = (await ListAsync(h)).Stations.Single(s => s.Id == "two");

        Assert.Null(two.ItemId);
        Assert.Null(two.ImageTag);
    }

    [Fact]
    public async Task The_tag_is_withheld_while_the_item_shows_another_origin_s_cover()
    {
        // The L4 rule, per station: "two"'s item still carries the cover of an earlier base URL; "live" is unaffected.
        var h = new MultiStationHarness();
        var earlier = StationUrls.Origin(new Uri("http://" + MultiStationHarness.HostDup + "/"));
        await h.Artwork.ApplyAsync(h.TwoItem, new PreparedArtwork("two", earlier, "cov1", new StationImage("image/png", ImageFixtures.Png(4, 4))), CancellationToken.None);

        var list = await ListAsync(h);
        var feed = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), "two"));

        Assert.Null(list.Stations.Single(s => s.Id == "two").ImageTag);
        Assert.Null(feed.ImageTag);
        Assert.Equal(FeedStationSwitchTests.TagProcessor.Tag, list.Stations.Single(s => s.Id == "live").ImageTag);
    }

    [Fact]
    public async Task The_tag_is_withheld_while_the_state_is_another_origin_s()
    {
        // The poller still holds "two"'s state under an earlier origin (its base URL just changed).
        var h = new MultiStationHarness();
        h.Config.Stations[1].BaseUrl = "http://" + MultiStationHarness.HostDup + ":8800";

        var two = (await ListAsync(h)).Stations.Single(s => s.Id == "two");

        Assert.Null(two.ImageTag);
        Assert.Equal(h.TwoItem.Id, two.ItemId);
    }
}
