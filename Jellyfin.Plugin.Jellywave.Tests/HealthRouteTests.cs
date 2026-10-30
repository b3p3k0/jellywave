using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14 review: <c>GET /Jellywave/Health</c> tells a refused saved URL from a missing one, so the status line
/// does not say "no SUB/WAVE station URL configured" while the field holds a URL.
/// </summary>
public class HealthRouteTests
{
    private static HealthResponse Health(string baseUrl) => Health(new PluginConfiguration { Stations = { new StationDefinition { Id = "live", BaseUrl = baseUrl } } });

    private static HealthResponse Health(PluginConfiguration config) => Health(config, new NowPlayingState(), new PollerHealth(), null, null);

    private static HealthResponse Health(PluginConfiguration config, NowPlayingState state, PollerHealth poller, LiveItemArtwork? artwork, StationLogos? logos)
        => Health(config, state, poller, artwork, logos, new StationTitles());

    private static HealthResponse Health(PluginConfiguration config, StationTitles titles) => Health(config, new NowPlayingState(), new PollerHealth(), null, null, titles);

    private static HealthResponse Health(PluginConfiguration config, NowPlayingState state, PollerHealth poller, LiveItemArtwork? artwork, StationLogos? logos, StationTitles titles)
    {
        var station = new StationClient(null!);   // never called by Health
        var controller = new JellywaveController(
            station,
            null!,
            state,
            poller,
            artwork ?? new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            logos ?? new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            new LiveItemFinder(FakeLibrary.Create().Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<JellywaveController>.Instance,
            () => config,
            titles);
        return controller.Health().Value!;
    }

    [Theory]
    [InlineData("http://radio.example:7700/?", StationUrls.RefusedQuery)]
    [InlineData("http://radio.example:7700/#", StationUrls.RefusedFragment)]
    [InlineData("http://listener:hunter2@radio.example:7700", StationUrls.RefusedCredentials)]
    public void A_refused_url_is_set_but_not_allowed(string baseUrl, string reason)
    {
        var health = Health(baseUrl);
        Assert.False(health.StationConfigured);
        Assert.Equal(reason, health.StationUrlRefusal);
        Assert.Equal("the SUB/WAVE station URL is set but not allowed: " + reason, health.StationUrlStatus);
        Assert.DoesNotContain("hunter2", health.StationUrlStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("listener", health.StationUrlStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_plain_stream_is_named_by_its_host_alone()
    {
        // Card L11: Health names a blank plain stream after its stream URL's host; port, path and query stay out.
        var config = new PluginConfiguration
        {
            Stations = { new StationDefinition { Id = "plain", Name = string.Empty, Kind = StationKind.Stream, StreamUrl = "https://cdn.example:8443/hidden-mount/p.mp3?token=secret-query" } }
        };

        var health = Health(config);

        Assert.Equal("cdn.example", Assert.Single(health.Stations).Name);
        var json = JsonSerializer.Serialize(health);

        // A port is banned with its colon: a bare "8443" can turn up in a timestamp's fraction of a second (fix round 2).
        foreach (var banned in new[] { "http", "://", ":8443", "hidden-mount", "p.mp3", "secret-query", "token" })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Card L13: a plain stream's last title read ----

    private static PluginConfiguration ReadStations() => new()
    {
        Stations =
        {
            new StationDefinition { Id = "sw", Name = "SubWave", BaseUrl = "http://sw.example:7700", SortOrder = 0 },
            new StationDefinition { Id = "up", Name = "Up", Kind = StationKind.Stream, StreamUrl = "https://up.example:8443/secret-mount/u.mp3?token=hunter2", SortOrder = 1 },
            new StationDefinition { Id = "down", Name = "Down", Kind = StationKind.Stream, StreamUrl = "http://down.example/d.mp3", SortOrder = 2 },
            new StationDefinition { Id = "hls", Name = "Hls", Kind = StationKind.Stream, StreamUrl = "https://hls.example/live/index.m3u8", SortOrder = 3 },
            new StationDefinition { Id = "unread", Name = "Unread", Kind = StationKind.Stream, StreamUrl = "http://unread.example/r.mp3", SortOrder = 4 },
            new StationDefinition { Id = "off", Name = "Off", Kind = StationKind.Stream, StreamUrl = "http://off.example/o.mp3", Enabled = false, SortOrder = 5 },
            new StationDefinition { Id = "moved", Name = "Moved", Kind = StationKind.Stream, StreamUrl = "http://moved.example/m.mp3", SortOrder = 6 },
        }
    };

    [Fact]
    public void A_plain_stream_reports_its_last_read_and_nothing_else_does()
    {
        var config = ReadStations();
        var titles = new StationTitles();
        var upUtc = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var downUtc = upUtc.AddHours(-2);
        titles.RecordRead("up", new Uri(config.Stations[1].StreamUrl), TitleReadOutcome.Answered, upUtc);
        titles.RecordRead("down", new Uri(config.Stations[2].StreamUrl), TitleReadOutcome.NoAnswer, downUtc);
        titles.RecordRead("moved", new Uri(config.Stations[6].StreamUrl), TitleReadOutcome.Moved, upUtc);
        // Reads recorded under ids that are not listed plain streams never show.
        titles.RecordRead("sw", new Uri("http://sw.example:7700/stream.mp3"), TitleReadOutcome.Answered, upUtc);
        titles.RecordRead("off", new Uri(config.Stations[5].StreamUrl), TitleReadOutcome.Answered, upUtc);

        var stations = Health(config, titles).Stations.ToDictionary(s => s.Id);

        Assert.Equal("Answered", stations["up"].LastReadOutcome);
        Assert.Equal(upUtc, stations["up"].LastReadUtc);
        Assert.Equal("NoAnswer", stations["down"].LastReadOutcome);
        Assert.Equal(downUtc, stations["down"].LastReadUtc);
        Assert.Equal("Moved", stations["moved"].LastReadOutcome);
        foreach (var id in new[] { "sw", "hls", "unread", "off" })
        {
            Assert.Null(stations[id].LastReadOutcome);
            Assert.Null(stations[id].LastReadUtc);
        }
    }

    [Fact]
    public void A_read_of_an_earlier_url_is_not_reported()
    {
        var config = ReadStations();
        var titles = new StationTitles();
        titles.RecordRead("up", new Uri("https://up.example/old.mp3"), TitleReadOutcome.Answered, DateTime.UtcNow);

        var up = Health(config, titles).Stations.Single(s => s.Id == "up");

        Assert.Null(up.LastReadOutcome);
        Assert.Null(up.LastReadUtc);
    }

    [Fact]
    public void Health_with_reads_carries_no_url()
    {
        var config = ReadStations();
        var titles = new StationTitles();
        foreach (var station in config.Stations.Where(s => s.Kind == StationKind.Stream))
        {
            titles.RecordRead(station.Id, new Uri(station.StreamUrl), station.Id == "down" ? TitleReadOutcome.NoAnswer : station.Id == "moved" ? TitleReadOutcome.Moved : TitleReadOutcome.Answered, DateTime.UtcNow);
        }

        var health = Health(config, titles);
        var json = JsonSerializer.Serialize(health);

        Assert.Contains("\"LastReadOutcome\":\"Answered\"", json, StringComparison.Ordinal);   // control: the reads are in it
        Assert.Contains("\"LastReadOutcome\":\"NoAnswer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"LastReadOutcome\":\"Moved\"", json, StringComparison.Ordinal);
        foreach (var banned in new[] { "http", "://", "up.example", "down.example", "moved.example", ":8443", "secret-mount", "u.mp3", "token", "hunter2", "m3u8" })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_blank_url_is_not_configured()
    {
        var health = Health("  ");
        Assert.False(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Equal("no SUB/WAVE station URL configured", health.StationUrlStatus);
    }

    [Fact]
    public void Plain_streams_only_say_there_is_no_sub_wave_station()
    {
        var health = Health(new PluginConfiguration
        {
            Stations = { new StationDefinition { Id = "a", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3" } }
        });

        Assert.False(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Equal("no SUB/WAVE station URL configured", health.StationUrlStatus);
        Assert.Single(health.Stations);
        Assert.True(health.Stations[0].Listed);
    }

    [Fact]
    public void A_usable_url_has_no_status_text()
    {
        var health = Health("http://radio.example:7700");
        Assert.True(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Null(health.StationUrlStatus);
        Assert.False(health.LiveItemExists);
    }

    [Fact]
    public void A_station_refused_for_another_reason_says_why_it_is_not_listed()
    {
        var health = Health(new PluginConfiguration
        {
            Stations =
            {
                new StationDefinition { Id = "live", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3" },
                new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700", SortOrder = 1 },
            }
        });

        Assert.False(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Equal("the SUB/WAVE station is not listed: " + StationSources.RefusedDuplicateId, health.StationUrlStatus);
    }

    /// <summary>Card L4: one of each kind of station the table shows.</summary>
    private static PluginConfiguration Mixed() => new()
    {
        Stations =
        {
            new StationDefinition { Id = "off", Name = "Off", BaseUrl = "http://off.example:7700", Enabled = false, SortOrder = 0 },
            new StationDefinition { Id = "plain", Name = "Plain", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3?token=hunter2", LogoUrl = "https://cdn.example/logo.png", SortOrder = 1 },
            new StationDefinition { Id = "sw1", Name = "Sub One", BaseUrl = "http://one.example:7700", SortOrder = 2 },
            new StationDefinition { Id = "sw2", Name = "Sub Two", BaseUrl = "http://two.example:7700", SortOrder = 3 },
            new StationDefinition { Id = "nologo", Name = "No Logo", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/b.mp3", SortOrder = 4 },
            new StationDefinition { Id = "badlogo", Name = "Bad Logo", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/c.mp3", LogoUrl = "ftp://cdn.example/x.png", SortOrder = 5 },
            new StationDefinition { Id = "pls", Name = "Playlist", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/list.pls", SortOrder = 6 },
            new StationDefinition { Id = "creds", Name = "Creds", BaseUrl = "http://listener:hunter2@secret.example:7700", SortOrder = 7 },
            new StationDefinition { Id = "sw1", Name = "Dup", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/d.mp3", SortOrder = 8 },
        }
    };

    [Fact]
    public async Task Every_configured_station_is_listed_in_sort_order_with_its_own_status()
    {
        var config = Mixed();
        config.Stations.Insert(0, config.Stations[8]);   // list order differs from SortOrder
        config.Stations.RemoveAt(9);
        var sw1 = new StationKey("sw1", "http://one.example:7700/");
        var sw2 = new StationKey("sw2", "http://two.example:7700/");
        var poller = new PollerHealth();
        var success = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        poller.Success(sw1, success);
        poller.Failure(sw2, success, StationFailures.TimedOut);
        var station = new StationClient(null!);
        var artwork = new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance);
        await artwork.ApplyAsync(new MediaBrowser.Controller.Entities.Audio.Audio(), LiveItemArtwork.Logo("sw2"), CancellationToken.None);
        var logos = new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System);
        var plain = config.Stations.Single(s => s.Id == "plain");
        await logos.ApplyAsync(new MediaBrowser.Controller.Entities.Audio.Audio(), new PreparedLogo("plain", StationLogos.KeyOf(plain), new StationImage("image/png", ImageFixtures.Png(8, 8))), CancellationToken.None);

        var health = Health(config, new NowPlayingState(), poller, artwork, logos);

        Assert.Equal(new[] { "off", "plain", "sw1", "sw2", "nologo", "badlogo", "pls", "creds", "sw1" }, health.Stations.Select(s => s.Id));
        var byName = health.Stations.ToDictionary(s => s.Name);
        Assert.Equal((false, false, StationSources.NotListedDisabled), (byName["Off"].Enabled, byName["Off"].Listed, byName["Off"].NotListedReason));
        Assert.Equal(("SubWave", "Stream"), (byName["Off"].Kind, byName["Plain"].Kind));
        Assert.Equal((true, (string?)null, StationLogos.StatusOwn), (byName["Plain"].Listed, byName["Plain"].NotListedReason, byName["Plain"].Logo));
        Assert.Equal((true, (bool?)true, (DateTime?)success, (string?)null), (byName["Sub One"].Listed, byName["Sub One"].Reachable, byName["Sub One"].LastSuccessUtc, byName["Sub One"].LastError));
        Assert.Equal(((bool?)false, (string?)StationFailures.TimedOut, (string?)LiveItemArtwork.LogoKey), (byName["Sub Two"].Reachable, byName["Sub Two"].LastError, byName["Sub Two"].Cover));
        Assert.Null(byName["Sub One"].Logo);
        Assert.Equal(StationLogos.StatusEmbedded, byName["No Logo"].Logo);
        Assert.Equal(StationLogos.StatusFailed, byName["Bad Logo"].Logo);
        Assert.Equal((false, StationSources.RefusedPlaylist), (byName["Playlist"].Listed, byName["Playlist"].NotListedReason));
        Assert.Equal(StationSources.RefusedBaseUrlPrefix + StationUrls.RefusedCredentials, byName["Creds"].NotListedReason);

        // A duplicate of a listed id gets none of that station's poll fields.
        var dup = byName["Dup"];
        Assert.Equal((false, StationSources.RefusedDuplicateId), (dup.Listed, dup.NotListedReason));
        Assert.Equal(((bool?)null, (DateTime?)null, (string?)null, (string?)null), (dup.Reachable, dup.LastSuccessUtc, dup.Cover, dup.Logo));

        // The top-level fields stay the primary station's (sw1), so today's page keeps working.
        Assert.Equal(((bool?)true, (DateTime?)success, true), (health.Reachable, health.LastSuccessUtc, health.StationConfigured));
    }

    [Fact]
    public void A_stream_whose_logo_waits_for_its_download_is_deferred()
    {
        var health = Health(Mixed());

        Assert.Equal(StationLogos.StatusDeferred, health.Stations.Single(s => s.Id == "plain").Logo);
    }

    [Fact]
    public void The_health_response_carries_no_url_and_no_credential()
    {
        var config = Mixed();
        var poller = new PollerHealth();
        poller.Failure(new StationKey("sw2", "http://two.example:7700/"), DateTime.UtcNow, StationFailures.Category(new HttpRequestException("Connection refused (two.example:7700)")));

        var json = JsonSerializer.Serialize(Health(config, new NowPlayingState(), poller, null, null));

        foreach (var secret in new[] { "://", "example", "hunter2", "listener", "token", "a.mp3", "logo.png", "list.pls", "x.png" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(StationFailures.CouldNotConnect, json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_station_polled_under_another_origin_shows_no_health_for_its_new_url()
    {
        var config = new PluginConfiguration { Stations = { new StationDefinition { Id = "live", BaseUrl = "http://new.example" } } };
        var poller = new PollerHealth();
        poller.Failure(new StationKey("live", "http://old.example/"), DateTime.UtcNow, StationFailures.TimedOut);

        var health = Health(config, new NowPlayingState(), poller, null, null);

        Assert.Null(health.Reachable);
        Assert.Null(health.LastError);
        Assert.Null(Assert.Single(health.Stations).LastError);
    }

    [Fact]
    public void Health_reports_how_long_the_last_fetch_phase_took()
    {
        var poller = new PollerHealth();
        Assert.Null(Health(new PluginConfiguration(), new NowPlayingState(), poller, null, null).LastFetchSeconds);

        poller.FetchTook(TimeSpan.FromSeconds(12.34));

        Assert.Equal(12.3, Health(new PluginConfiguration(), new NowPlayingState(), poller, null, null).LastFetchSeconds);
    }
}
