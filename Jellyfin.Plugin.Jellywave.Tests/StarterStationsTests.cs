using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L9: the starter stations a fresh install gets. They go through the same rules as a station the admin adds
/// (<see cref="StationSources"/>, <see cref="StationUrls"/>), their format fields agree with each other and with the
/// stream address, and (card R2a) their ids are random on every seed, never one of the publicly known ids. When the plugin
/// seeds them: <see cref="PluginWiringTests"/>.
/// </summary>
public class StarterStationsTests
{
    // Pinned: a changed name or order changes what a fresh install shows. The format is what ffprobe reported for each
    // stream (checked by hand, card L9); a wrong one makes the client transcode or refuse the stream. The ids are random
    // (card R2a), so the rows are matched by name.
    private static readonly (string Name, StationKind Kind, string Codec, int BitrateKbps, int SampleRate)[] Expected =
    [
        ("SUB/WAVE demo", StationKind.SubWave, "mp3", 192, 44100),
        ("KEXP", StationKind.Stream, "aac", 160, 44100),
        ("WRUW 91.1 FM", StationKind.Stream, "mp3", 128, 48000),
        ("WJCU 88.7 FM", StationKind.Stream, "mp3", 128, 44100),
        ("XCSB", StationKind.Stream, "mp3", 160, 44100)
    ];

    private static PluginConfiguration Seeded()
    {
        var config = new PluginConfiguration { SchemaVersion = PluginConfiguration.CurrentSchemaVersion };
        Assert.True(StarterStations.Seed(config));
        return config;
    }

    [Fact]
    public void The_list_is_the_approved_one_in_order()
    {
        var stations = StarterStations.Create();

        Assert.Equal(Expected.Select(e => (e.Name, e.Kind)), stations.Select(s => (s.Name, s.Kind)));
        Assert.Equal(Enumerable.Range(0, stations.Count), stations.Select(s => s.SortOrder));
        Assert.All(stations, s => Assert.True(s.Enabled, s.Name));
    }

    [Fact]
    public void Ids_are_random_guids_in_the_page_format_and_two_seeds_differ()
    {
        var stations = StarterStations.Create();
        var again = StarterStations.Create();

        // The page's newId(): a GUID in "N" format. Distinct ignoring case (Jellyfin matches external ids that way).
        Assert.All(stations, s => Assert.True(Guid.TryParseExact(s.Id, "N", out _), s.Name));
        Assert.Equal(stations.Count, stations.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Card R2a: no install shares an id with another, so no item id can be computed from public facts.
        Assert.Empty(stations.Select(s => s.Id).Intersect(again.Select(s => s.Id), StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(stations.Concat(again), s => StationIds.KnownPublicIds.Contains(s.Id));
    }

    [Fact]
    public void Every_call_returns_new_instances()
    {
        var first = StarterStations.Create();
        first[0].Name = "changed";
        first[1].Enabled = false;

        var second = StarterStations.Create();

        Assert.Equal("SUB/WAVE demo", second[0].Name);
        Assert.True(second[1].Enabled);
        Assert.DoesNotContain(second, s => first.Contains(s));
    }

    [Fact]
    public void Every_starter_passes_the_station_rules_and_is_listed()
    {
        var config = Seeded();

        Assert.All(config.Stations, s => Assert.Null(StationSources.Refusal(s)));
        var listed = StationSources.Publish(config, (s, why) => Assert.Fail(s.Name + ": " + why));
        Assert.Equal(config.Stations, listed.Select(l => l.Station));
        Assert.Same(config.Stations[0], config.PrimaryStation());   // the SUB/WAVE demo serves the tile's description
    }

    [Fact]
    public void Every_starter_url_passes_the_url_rules()
    {
        foreach (var s in StarterStations.Create())
        {
            Assert.Null(StationUrls.WebsiteRefusal(s.Website));
            if (s.Kind == StationKind.SubWave)
            {
                Assert.Null(StationUrls.BaseUrlRefusal(s.BaseUrl));
                Assert.True(StationUrls.TryGetServerStreamUrl(s, out var url), s.Name);
                Assert.Equal("https://www.getsubwave.com/stream.mp3", url!.AbsoluteUri);
                Assert.Empty(s.StreamUrl);
                Assert.Empty(s.LogoUrl);   // a SUB/WAVE station shows the track cover
            }
            else
            {
                Assert.Null(StationUrls.StreamUrlRefusal(s.StreamUrl));
                Assert.True(StationUrls.TryParseStreamUrl(s.StreamUrl, out var url), s.Name);
                Assert.Equal(s.StreamUrl, url!.AbsoluteUri);   // what reaches ffmpeg is the URL as written
                Assert.True(StationUrls.TryParseLogoUrl(s.LogoUrl, out var logo), s.Name);
                Assert.Equal(s.LogoUrl, logo!.AbsoluteUri);
                Assert.Empty(s.BaseUrl);
                Assert.True(StationUrls.WebsiteLink(s.Website) is not null, s.Name);
            }
        }
    }

    [Fact]
    public void Every_starter_names_a_public_host()
    {
        // The CLAUDE.md exemption covers public station hosts only: no IP literal, no single-label or local name.
        foreach (var s in StarterStations.Create())
        {
            foreach (var raw in new[] { s.BaseUrl, s.StreamUrl, s.LogoUrl, s.Website }.Where(u => u.Length > 0))
            {
                var host = new Uri(raw).Host;
                Assert.Equal(UriHostNameType.Dns, Uri.CheckHostName(host));
                Assert.False(IPAddress.TryParse(host, out _), raw);
                Assert.Contains('.', host);
                Assert.False(host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase), raw);
            }
        }
    }

    [Fact]
    public void Every_starter_format_is_consistent()
    {
        var config = Seeded();
        foreach (var source in StationSources.Publish(config))
        {
            var s = source.Station;

            // The listed source serves exactly the fields as written: no normalising, no HLS guess.
            Assert.Equal(s.Codec, source.Codec);
            Assert.Equal(s.Container, source.Container);
            Assert.False(source.IsHls, s.Name);
            Assert.Equal(s.Codec, s.Container);
            Assert.Contains(s.Codec, new[] { "mp3", "aac" });

            // The stream address names the codec (".mp3", ".aac", "-mp3-hi").
            Assert.Contains(s.Codec, source.ServerUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);

            // Each starter's own format, not just a plausible one: WRUW at 44100 or KEXP at 128 would fail here.
            var expected = Assert.Single(Expected, e => e.Name == s.Name);
            Assert.Equal((expected.Codec, expected.BitrateKbps, expected.SampleRate), (s.Codec, s.BitrateKbps, s.SampleRate));
            Assert.Equal(2, s.Channels);
            Assert.InRange(s.Genre.Length, 0, StationDefinition.MaxGenreLength);
            Assert.InRange(s.Notes.Length, 0, StationDefinition.MaxNotesLength);
        }
    }

    [Fact]
    public void Seed_adds_the_starters_to_an_empty_list_only()
    {
        var empty = new PluginConfiguration();
        Assert.True(StarterStations.Seed(empty));
        Assert.Equal(Expected.Select(e => e.Name), empty.Stations.Select(s => s.Name));

        // A second call, or a list the admin has, is left alone: the starters never land twice or next to a station.
        Assert.False(StarterStations.Seed(empty));
        Assert.Equal(Expected.Length, empty.Stations.Count);

        var own = new PluginConfiguration { Stations = { new StationDefinition { Id = "mine", Kind = StationKind.Stream } } };
        Assert.False(StarterStations.Seed(own));
        Assert.Equal("mine", Assert.Single(own.Stations).Id);

        var nullList = new PluginConfiguration { Stations = null! };
        Assert.True(StarterStations.Seed(nullList));
        Assert.Equal(Expected.Length, nullList.Stations.Count);
    }

    [Fact]
    public void A_new_configuration_starts_with_no_stations()
    {
        // The XmlSerializer trap: a list filled in the constructor gets the saved entries appended on load, so the
        // starters would come back twice and a removed one would come back. The constructor must leave it empty.
        Assert.Empty(new PluginConfiguration().Stations);
        Assert.IsType<Collection<StationDefinition>>(new PluginConfiguration().Stations);
    }
}
