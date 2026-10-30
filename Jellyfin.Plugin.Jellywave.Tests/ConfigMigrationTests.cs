using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L1 (diagram 9): a 0.4.x configuration file moves onto the Live Radio shape. The files are read the way Jellyfin's
/// <c>MyXmlSerializer</c> reads them (<c>new XmlSerializer(type)</c> through <c>XmlReader.Create</c>), and channel ids are
/// derived with Jellyfin 12.3's rule (<see cref="FakeLibrary.IdFor"/>).
/// </summary>
public class ConfigMigrationTests
{
    // Written by 0.4.2.0 for a configured station (every public settable property, in declaration order).
    private const string Configured042 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
          <StreamPath>/stream.mp3</StreamPath>
          <ClientStreamUrl>https://public.example/stream.mp3</ClientStreamUrl>
          <StationName>IHateRadio</StationName>
          <StationTagline>late-night static</StationTagline>
          <Delivery>Direct</Delivery>
          <NowPlayingEnabled>true</NowPlayingEnabled>
          <CoverArtEnabled>false</CoverArtEnabled>
          <NowPlayingPollSeconds>30</NowPlayingPollSeconds>
          <StreamBitrateKbps>128</StreamBitrateKbps>
          <StreamSampleRate>48000</StreamSampleRate>
          <StreamChannels>1</StreamChannels>
          <LastChangedUtc>2026-10-27T18:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    // Written by 0.3.4.0 (still carries the removed grant setting).
    private const string WithGrantSetting = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
          <StreamPath>/stream.mp3</StreamPath>
          <ClientStreamUrl />
          <StationName>Test Radio</StationName>
          <StationTagline>tagline</StationTagline>
          <Delivery>Relay</Delivery>
          <NowPlayingEnabled>false</NowPlayingEnabled>
          <CoverArtEnabled>true</CoverArtEnabled>
          <NowPlayingPollSeconds>30</NowPlayingPollSeconds>
          <StreamBitrateKbps>192</StreamBitrateKbps>
          <StreamSampleRate>44100</StreamSampleRate>
          <StreamChannels>2</StreamChannels>
          <GrantAccessToAllUsers>true</GrantAccessToAllUsers>
          <LastChangedUtc>2026-10-27T18:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    // Written by 0.4.2.0's BasePlugin on first load, before any save: the defaults.
    private const string NeverConfigured042 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl />
          <StreamPath>/stream.mp3</StreamPath>
          <ClientStreamUrl />
          <StationName>SUB/WAVE</StationName>
          <StationTagline />
          <Delivery>Relay</Delivery>
          <NowPlayingEnabled>true</NowPlayingEnabled>
          <CoverArtEnabled>true</CoverArtEnabled>
          <NowPlayingPollSeconds>20</NowPlayingPollSeconds>
          <StreamBitrateKbps>192</StreamBitrateKbps>
          <StreamSampleRate>44100</StreamSampleRate>
          <StreamChannels>2</StreamChannels>
          <LastChangedUtc>1970-01-01T00:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    // Written by the Live Radio branch before card L10: one SUB/WAVE and one plain stream, both set to Direct.
    private const string LiveRadioWithDirect = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <LibraryName>Live Radio</LibraryName>
          <Stations>
            <StationDefinition>
              <Id>live</Id>
              <Name>IHateRadio</Name>
              <Kind>SubWave</Kind>
              <Enabled>true</Enabled>
              <SortOrder>0</SortOrder>
              <BaseUrl>http://radio.example:7700</BaseUrl>
              <StreamPath>/stream.mp3</StreamPath>
              <StreamUrl />
              <ClientStreamUrl>https://public.example/stream.mp3</ClientStreamUrl>
              <Delivery>Direct</Delivery>
              <Codec>mp3</Codec>
              <Container>mp3</Container>
            </StationDefinition>
            <StationDefinition>
              <Id>plain</Id>
              <Name>Plain</Name>
              <Kind>Stream</Kind>
              <Enabled>true</Enabled>
              <SortOrder>1</SortOrder>
              <StreamUrl>https://cdn.example/live.aac?x=1</StreamUrl>
              <ClientStreamUrl />
              <Delivery>Direct</Delivery>
              <Codec>aac</Codec>
              <Container>aac</Container>
            </StationDefinition>
          </Stations>
          <PreviousChannelNames>
            <string>IHateRadio</string>
          </PreviousChannelNames>
          <SchemaVersion>1</SchemaVersion>
          <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
          <ClientStreamUrl>https://public.example/stream.mp3</ClientStreamUrl>
          <Delivery>Direct</Delivery>
        </PluginConfiguration>
        """;

    // Saved before card 14 with a base URL card 14 refuses (a bare query).
    private const string RefusedUrl042 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl>http://radio.example:7700/?</StationBaseUrl>
          <StreamPath>/stream.mp3</StreamPath>
          <StationName>   </StationName>
        </PluginConfiguration>
        """;

    // Jellyfin's PluginsController deserializes the page's POST with System.Text.Json (string enums, PascalCase).
    private static readonly JsonSerializerOptions PageJson = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly FakeLibrary Library = FakeLibrary.Create();

    private static Guid Id(string name) => ChannelIds.Derive(Library.Manager, name);

    private static PluginConfiguration Load(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var reader = XmlReader.Create(stream);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    private static (PluginConfiguration Config, List<string> Unknown) LoadReporting(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var unknown = new List<string>();
        serializer.UnknownElement += (_, e) => unknown.Add(e.Element.Name);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var reader = XmlReader.Create(stream);
        return ((PluginConfiguration)serializer.Deserialize(reader)!, unknown);
    }

    private static List<ChannelItemInfo> Items(PluginConfiguration config)
        => new JellywaveChannel(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), null!, new RenameBridge(_ => Guid.Empty), () => config)
            .GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None).GetAwaiter().GetResult().Items.ToList();

    // UTF-8 like the configuration file (a StringWriter would declare UTF-16, which the reader above cannot switch to).
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
    public void A_configured_042_file_becomes_a_station_with_a_random_id_and_the_old_tile_a_previous_name()
    {
        var config = Load(Configured042);
        Assert.Equal(0, config.SchemaVersion);

        Assert.True(ConfigMigration.Migrate(config, Id));

        var station = Assert.Single(config.Stations);

        // Card R2a: a random id, never the 0.4.x "live" every install shared (it made the item id computable).
        Assert.True(Guid.TryParseExact(station.Id, "N", out _), station.Id);
        Assert.DoesNotContain(new[] { station.Id }, StationIds.KnownPublicIds.Contains);
        var again = Load(Configured042);
        Assert.True(ConfigMigration.Migrate(again, Id));
        Assert.NotEqual(station.Id, Assert.Single(again.Stations).Id);
        Assert.Equal(StationKind.SubWave, station.Kind);
        Assert.Equal("IHateRadio", station.Name);
        Assert.True(station.Enabled);
        Assert.Equal(0, station.SortOrder);
        Assert.Equal("http://radio.example:7700", station.BaseUrl);
        Assert.Equal("/stream.mp3", station.StreamPath);
        Assert.Equal("late-night static", station.Tagline);
        Assert.Equal("mp3", station.Codec);
        Assert.Equal("mp3", station.Container);
        Assert.Equal(128, station.BitrateKbps);
        Assert.Equal(48000, station.SampleRate);
        Assert.Equal(1, station.Channels);
        Assert.Equal(string.Empty, station.StreamUrl);

        Assert.Equal("Live Radio", config.LibraryName);
        Assert.Equal(new[] { "IHateRadio" }, config.PreviousChannelNames);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Same(station, config.PrimaryStation());

        // Global settings are untouched.
        Assert.True(config.NowPlayingEnabled);
        Assert.False(config.CoverArtEnabled);
        Assert.Equal(30, config.NowPlayingPollSeconds);
        Assert.Equal(new DateTime(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc), config.LastChangedUtc.ToUniversalTime());
    }

    [Fact]
    public void The_legacy_fields_are_kept_and_saved_for_a_downgrade()
    {
        var config = Load(Configured042);
        ConfigMigration.Migrate(config, Id);

        Assert.Equal("http://radio.example:7700", config.StationBaseUrl);
        Assert.Equal("IHateRadio", config.StationName);

        // 0.4.2.0 reads the saved file: its XmlSerializer skips the new elements and finds the old ones unchanged. The
        // Direct fields are gone since card L10, so a downgrade reads 0.4.x's defaults for them: Relay, no client URL.
        var saved = Save(config);
        Assert.Contains("<StationBaseUrl>http://radio.example:7700</StationBaseUrl>", saved, StringComparison.Ordinal);
        Assert.Contains("<StationName>IHateRadio</StationName>", saved, StringComparison.Ordinal);
        Assert.Contains("<StreamBitrateKbps>128</StreamBitrateKbps>", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Delivery", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientStreamUrl", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("public.example", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_042_file_saved_in_direct_loads_cleanly_and_its_station_is_relayed()
    {
        // Card L10: Delivery and ClientStreamUrl are unknown elements now. Jellyfin's XmlSerializer reports and skips
        // them (BasePlugin would replace an unreadable file with defaults, so they must not throw).
        var (config, unknown) = LoadReporting(Configured042);
        Assert.Equal(new[] { "ClientStreamUrl", "Delivery" }, unknown);

        Assert.True(ConfigMigration.Migrate(config, Id));

        var source = Assert.Single(StationSources.Publish(config));
        Assert.Equal("http://radio.example:7700/stream.mp3", source.ServerUrl.AbsoluteUri);
        var media = Assert.Single(Assert.Single(Items(config)).MediaSources);
        Assert.False(media.SupportsDirectPlay);
        Assert.Equal("http://radio.example:7700/stream.mp3", media.Path);   // the server URL, never the 0.4.x client URL
        Assert.Null(media.EncoderPath);
    }

    [Fact]
    public void A_live_radio_file_saved_with_direct_stations_loads_cleanly_and_relays_them()
    {
        // Card L10: a file written by the Live Radio branch before L10 carries Delivery and ClientStreamUrl per station.
        var (config, unknown) = LoadReporting(LiveRadioWithDirect);
        Assert.Equal(new[] { "ClientStreamUrl", "Delivery", "ClientStreamUrl", "Delivery", "ClientStreamUrl", "Delivery" }, unknown);   // two stations, the legacy pair
        Assert.False(ConfigMigration.Migrate(config, Id));   // already the current shape

        var items = Items(config);
        Assert.Equal(new[] { "live", "plain" }, items.Select(i => i.Id));
        Assert.All(items, i => Assert.False(Assert.Single(i.MediaSources).SupportsDirectPlay));
        Assert.Equal(
            new[] { "http://radio.example:7700/stream.mp3", "https://cdn.example/live.aac?x=1" },
            items.Select(i => Assert.Single(i.MediaSources).Path));
        Assert.All(items, i => Assert.Null(Assert.Single(i.MediaSources).EncoderPath));

        // The next save drops both elements.
        var saved = Save(config);
        Assert.DoesNotContain("Delivery", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientStreamUrl", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("public.example", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_post_with_the_old_delivery_fields_still_saves()
    {
        // An older cached configuration page posts Delivery and ClientStreamUrl; System.Text.Json skips unknown members.
        const string Json = """
            { "SchemaVersion": 1, "LibraryName": "Live Radio",
              "Stations": [ { "Id": "live", "Kind": "SubWave", "Enabled": true, "BaseUrl": "http://radio.example:7700", "StreamPath": "/stream.mp3",
                              "Delivery": "Direct", "ClientStreamUrl": "https://public.example/s.mp3", "Codec": "mp3", "Container": "mp3" } ] }
            """;

        var config = JsonSerializer.Deserialize<PluginConfiguration>(Json, PageJson)!;

        Assert.Equal("http://radio.example:7700", Assert.Single(config.Stations).BaseUrl);
        Assert.False(Assert.Single(Assert.Single(Items(config)).MediaSources).SupportsDirectPlay);
    }

    [Fact]
    public void A_file_with_the_removed_grant_setting_still_loads_and_migrates()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var unknown = new List<string>();
        serializer.UnknownElement += (_, e) => unknown.Add(e.Element.Name);
        PluginConfiguration config;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(WithGrantSetting)))
        using (var reader = XmlReader.Create(stream))
        {
            config = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        Assert.Equal(new[] { "ClientStreamUrl", "Delivery", "GrantAccessToAllUsers" }, unknown);
        Assert.True(ConfigMigration.Migrate(config, Id));
        Assert.Equal("Test Radio", Assert.Single(config.Stations).Name);
        Assert.Equal(new[] { "Test Radio" }, config.PreviousChannelNames);
        Assert.DoesNotContain("GrantAccessToAllUsers", Save(config), StringComparison.Ordinal);
    }

    [Fact]
    public void A_never_configured_file_gets_no_station_but_records_the_empty_tile()
    {
        // 0.4.x listed the "SUB/WAVE" tile even without a URL, so an admin may have blocked it.
        var config = Load(NeverConfigured042);

        Assert.True(ConfigMigration.Migrate(config, Id));

        Assert.Empty(config.Stations);
        Assert.Null(config.PrimaryStation());
        Assert.Equal(new[] { "SUB/WAVE" }, config.PreviousChannelNames);
        Assert.Equal("Live Radio", config.LibraryName);
        Assert.Equal(1, config.SchemaVersion);
    }

    [Fact]
    public void A_fresh_install_migrates_the_defaults_the_same_way()
    {
        var config = new PluginConfiguration();

        Assert.True(ConfigMigration.Migrate(config, Id));

        Assert.Empty(config.Stations);
        Assert.Equal(new[] { "SUB/WAVE" }, config.PreviousChannelNames);
        Assert.Equal(1, config.SchemaVersion);
    }

    [Fact]
    public void A_url_card_14_refuses_is_migrated_as_saved_and_still_reported()
    {
        var config = Load(RefusedUrl042);

        Assert.True(ConfigMigration.Migrate(config, Id));

        var station = Assert.Single(config.Stations);
        Assert.Equal("http://radio.example:7700/?", station.BaseUrl);
        Assert.Equal("SUB/WAVE", station.Name);   // a blank 0.4.x name meant the fallback
        Assert.Equal(new[] { "SUB/WAVE" }, config.PreviousChannelNames);
        Assert.False(StationUrls.TryGetBaseUrl(config, out _));
        Assert.Null(config.PrimaryStation());   // not listed, so not served (card L2)
        Assert.Equal(StationUrls.RefusedQuery, StationUrls.BaseUrlRefusal(config.PrimaryCandidate()!.BaseUrl));
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        var config = Load(Configured042);
        Assert.True(ConfigMigration.Migrate(config, Id));
        var once = Save(config);

        Assert.False(ConfigMigration.Migrate(config, Id));
        Assert.Equal(once, Save(config));

        // Also after a save and reload, which is what the next start sees.
        var reloaded = Load(once);
        Assert.False(ConfigMigration.Migrate(reloaded, Id));
        Assert.Equal(once, Save(reloaded));
    }

    [Fact]
    public void An_old_tile_already_named_live_radio_is_not_a_previous_name()
    {
        var config = new PluginConfiguration { StationName = "Live Radio", StationBaseUrl = "http://radio.example" };
        ConfigMigration.Migrate(config, Id);
        Assert.Empty(config.PreviousChannelNames);
    }

    [Fact]
    public void An_old_name_that_derives_the_new_id_is_not_a_previous_name()
    {
        // With EnableCaseSensitiveItemIds off, "live radio" and "Live Radio" are one channel.
        var library = FakeLibrary.Create();
        library.CaseSensitiveIds = false;
        var config = new PluginConfiguration { StationName = "live radio" };

        ConfigMigration.Migrate(config, name => ChannelIds.Derive(library.Manager, name));

        Assert.Empty(config.PreviousChannelNames);
    }

    [Fact]
    public void The_new_shape_round_trips_through_the_xml_serializer()
    {
        var config = new PluginConfiguration
        {
            LibraryName = "My Radio",
            SchemaVersion = 1,
            PreviousChannelNames = { "IHateRadio", "SUB/WAVE" },
            Stations =
            {
                new StationDefinition { Id = "live", Name = "IHateRadio", BaseUrl = "http://radio.example:7700", Tagline = "t", BitrateKbps = 128 },
                new StationDefinition { Id = "c07e", Name = "Radio Paradise", Kind = StationKind.Stream, Enabled = false, SortOrder = 2, StreamUrl = "https://stream.example/mp3?x=1", LogoUrl = "https://stream.example/logo.png", Codec = "aac", Container = "aac", SampleRate = 48000, Channels = 1 }
            }
        };

        var copy = Load(Save(config));

        Assert.Equal("My Radio", copy.LibraryName);
        Assert.Equal(1, copy.SchemaVersion);
        Assert.Equal(new[] { "IHateRadio", "SUB/WAVE" }, copy.PreviousChannelNames);
        Assert.Equal(2, copy.Stations.Count);
        Assert.Equal(Fields(config.Stations[0]), Fields(copy.Stations[0]));
        Assert.Equal(Fields(config.Stations[1]), Fields(copy.Stations[1]));
        Assert.Equal(Save(config), Save(copy));
    }

    [Fact]
    public void The_new_shape_round_trips_through_the_page_save_json()
    {
        // Jellyfin's PluginsController deserializes the page's POST with System.Text.Json (string enums, PascalCase).
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var config = new PluginConfiguration
        {
            SchemaVersion = 1,
            PreviousChannelNames = { "IHateRadio" },
            Stations = { new StationDefinition { Id = "live", Name = "IHateRadio", BaseUrl = "http://radio.example:7700", Kind = StationKind.SubWave } }
        };

        var json = JsonSerializer.Serialize(config, options);
        Assert.Contains("\"Kind\":\"SubWave\"", json, StringComparison.Ordinal);
        var copy = JsonSerializer.Deserialize<PluginConfiguration>(json, options)!;

        Assert.Equal(new[] { "IHateRadio" }, copy.PreviousChannelNames);
        Assert.Equal(Fields(config.Stations[0]), Fields(Assert.Single(copy.Stations)));
        Assert.Equal(1, copy.SchemaVersion);
    }

    [Fact]
    public void A_rename_records_the_outgoing_name_once()
    {
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" } };
        var incoming = new PluginConfiguration { LibraryName = "Kevin Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" } };

        Assert.True(ConfigMigration.CaptureRename(current, incoming, Id));
        Assert.Equal(new[] { "IHateRadio", "Live Radio" }, incoming.PreviousChannelNames);

        // The same save again (the page posts what it read back) adds nothing.
        var again = new PluginConfiguration { LibraryName = "Kevin Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio", "Live Radio" } };
        Assert.False(ConfigMigration.CaptureRename(incoming, again, Id));
        Assert.Equal(new[] { "IHateRadio", "Live Radio" }, again.PreviousChannelNames);
    }

    [Fact]
    public void A_save_without_a_rename_records_nothing()
    {
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1 };
        var incoming = new PluginConfiguration { LibraryName = "  Live Radio ", SchemaVersion = 1 };

        Assert.False(ConfigMigration.CaptureRename(current, incoming, Id));
        Assert.Empty(incoming.PreviousChannelNames);
    }

    [Fact]
    public void A_save_that_drops_the_list_cannot_lift_the_old_blocks()
    {
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio", "SUB/WAVE" } };
        var incoming = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1, PreviousChannelNames = null! };

        ConfigMigration.CaptureRename(current, incoming, Id);

        Assert.Equal(new[] { "IHateRadio", "SUB/WAVE" }, incoming.PreviousChannelNames);
    }

    [Fact]
    public void A_case_only_rename_under_case_insensitive_ids_is_not_recorded()
    {
        var library = FakeLibrary.Create();
        library.CaseSensitiveIds = false;
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1 };
        var incoming = new PluginConfiguration { LibraryName = "LIVE RADIO", SchemaVersion = 1 };

        Assert.False(ConfigMigration.CaptureRename(current, incoming, name => ChannelIds.Derive(library.Manager, name)));
        Assert.Empty(incoming.PreviousChannelNames);
    }

    [Fact]
    public void A_posted_body_in_the_legacy_shape_is_migrated_before_the_save()
    {
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" } };
        var incoming = new PluginConfiguration { StationName = "Other", StationBaseUrl = "http://other.example" };   // SchemaVersion 0

        ConfigMigration.PrepareSave(current, incoming, Id);

        Assert.Equal(1, incoming.SchemaVersion);
        Assert.Equal("http://other.example", Assert.Single(incoming.Stations).BaseUrl);
        Assert.Equal(new[] { "Other", "IHateRadio" }, incoming.PreviousChannelNames);
    }

    [Fact]
    public void Migration_keeps_an_existing_station_list_and_never_adds_a_second_live()
    {
        // A file that already holds stations but no schema version (hand-edited, or written by a client in between).
        var other = new StationDefinition { Id = "c07e", Name = "Radio Paradise", Kind = StationKind.Stream, StreamUrl = "https://stream.example/mp3" };
        var live = new StationDefinition { Id = "live", Name = "Kept", BaseUrl = "http://kept.example" };
        var config = new PluginConfiguration { StationBaseUrl = "http://radio.example", StationName = "IHateRadio", Stations = { other, live } };

        Assert.True(ConfigMigration.Migrate(config, Id));

        Assert.Equal(new[] { other, live }, config.Stations);
        Assert.Equal("http://kept.example", config.Stations.Single(s => s.Id == "live").BaseUrl);
    }

    [Fact]
    public void Migration_does_not_add_the_station_again_when_one_already_has_its_base_url()
    {
        // Card R2a: the migrated station's id is random now, so "already there" is also told by its base URL.
        var kept = new StationDefinition { Id = "9b11", Kind = StationKind.SubWave, BaseUrl = "http://radio.example" };
        var config = new PluginConfiguration { StationBaseUrl = " http://radio.example ", Stations = { kept } };

        Assert.True(ConfigMigration.Migrate(config, Id));

        Assert.Same(kept, Assert.Single(config.Stations));
    }

    [Fact]
    public void Migration_adds_live_in_front_of_stations_already_listed()
    {
        var other = new StationDefinition { Id = "c07e", Kind = StationKind.Stream, StreamUrl = "https://stream.example/mp3" };
        var config = new PluginConfiguration { StationBaseUrl = "http://radio.example", Stations = { other } };

        ConfigMigration.Migrate(config, Id);

        Assert.Equal(2, config.Stations.Count);
        Assert.Equal(StationKind.SubWave, config.Stations[0].Kind);
        Assert.Equal("http://radio.example", config.Stations[0].BaseUrl);
        Assert.Same(other, config.Stations[1]);
    }

    [Fact]
    public void A_failing_id_rule_leaves_the_configuration_exactly_as_loaded()
    {
        // S1: the plugin then runs it unmigrated; a half migration (tile renamed, no bridge) would lift old blocks.
        var config = Load(Configured042);
        var before = Save(config);

        Assert.Throws<InvalidOperationException>(() => ConfigMigration.Migrate(config, _ => throw new InvalidOperationException("no ids")));

        Assert.Equal(before, Save(config));
        Assert.Equal(0, config.SchemaVersion);
    }

    [Fact]
    public void A_version_0_body_with_the_new_shape_keeps_its_tile_name_and_stations()
    {
        // N2: only a body with nothing of the new shape is migrated; otherwise it is kept and marked current.
        var current = new PluginConfiguration { LibraryName = "Live Radio", SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" } };
        var station = new StationDefinition { Id = "9b11", BaseUrl = "http://two.example" };
        var incoming = new PluginConfiguration { LibraryName = "Kevin Radio", StationName = "Legacy", StationBaseUrl = "http://legacy.example", Stations = { station } };

        ConfigMigration.PrepareSave(current, incoming, Id);

        Assert.Equal("Kevin Radio", incoming.LibraryName);
        Assert.Equal(new[] { station }, incoming.Stations);
        Assert.Equal(1, incoming.SchemaVersion);
        Assert.Equal(new[] { "IHateRadio", "Live Radio" }, incoming.PreviousChannelNames);
        Assert.False(ConfigMigration.Migrate(incoming, Id));   // the next start leaves it alone
    }

    [Theory]
    [InlineData("Kevin Radio", false)]   // a tile name only
    [InlineData("Live Radio", true)]     // a station only
    public void A_version_0_body_with_either_part_of_the_new_shape_is_not_migrated(string tile, bool withStation)
    {
        var current = new PluginConfiguration { SchemaVersion = 1 };
        var incoming = new PluginConfiguration { LibraryName = tile, StationName = "Legacy", StationBaseUrl = "http://legacy.example" };
        if (withStation)
        {
            incoming.Stations.Add(new StationDefinition { Id = "9b11", BaseUrl = "http://two.example" });
        }

        ConfigMigration.PrepareSave(current, incoming, Id);

        Assert.Equal(tile, incoming.LibraryName);
        Assert.DoesNotContain(incoming.Stations, s => s.Id == "live");
        Assert.DoesNotContain("Legacy", incoming.PreviousChannelNames);
        Assert.Equal(1, incoming.SchemaVersion);
    }

    [Fact]
    public void A_page_save_of_an_unmigrated_in_memory_configuration_keeps_the_legacy_tile()
    {
        // F-R1 (RA probe): after a failed migration the plugin runs the 0.4.x file as loaded. The page posts what it
        // loaded (schema 0) plus Stations[0] built from the form, so the body is kept, not migrated; the 0.4.x tile name
        // must still become an earlier name, or this save lifts the old blocks.
        var current = new PluginConfiguration { StationName = "IHateRadio", StationBaseUrl = "http://radio.example" };
        var bridge = new RenameBridge(Id);
        Assert.Equal(new[] { Id("IHateRadio") }, bridge.PreviousIds(current));

        var incoming = new PluginConfiguration { StationName = "IHateRadio", StationBaseUrl = "http://radio.example" };
        incoming.Stations.Add(new StationDefinition { Id = "live", BaseUrl = "http://radio.example" });

        ConfigMigration.PrepareSave(current, incoming, Id);

        Assert.Equal(1, incoming.SchemaVersion);
        Assert.Equal(new[] { "IHateRadio" }, incoming.PreviousChannelNames);
        Assert.Equal(new[] { Id("IHateRadio") }, bridge.PreviousIds(incoming));
    }

    [Fact]
    public void Carried_names_survive_a_save_that_drops_them()
    {
        var current = new PluginConfiguration { SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" }, CarriedChannelNames = { "IHateRadio" } };
        var incoming = new PluginConfiguration { SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" }, CarriedChannelNames = null! };

        ConfigMigration.PrepareSave(current, incoming, Id);

        Assert.Equal(new[] { "IHateRadio" }, incoming.CarriedChannelNames);
        Assert.False(ConfigMigration.AddCarriedName(incoming, " IHateRadio ", Id));
    }

    [Fact]
    public void The_plugin_hooks_the_configuration_save()
    {
        // Jellyfin 12.3's PluginsController.UpdatePluginConfiguration calls IHasPluginConfiguration.UpdateConfiguration.
        var method = typeof(Plugin).GetMethod(nameof(BasePlugin<PluginConfiguration>.UpdateConfiguration), [typeof(BasePluginConfiguration)])!;
        Assert.Equal(typeof(Plugin), method.DeclaringType);
    }

    private static string Fields(StationDefinition s)
        => string.Join(
            '|',
            typeof(StationDefinition).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + p.GetValue(s)));
}
