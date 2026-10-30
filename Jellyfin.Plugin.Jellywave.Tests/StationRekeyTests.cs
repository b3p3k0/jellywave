using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a (HI decision B): a station id that is the same on every install (<c>live</c>, the starters' former ids) makes
/// its item id computable, and Jellyfin streams an item without sign-in. At startup every such id gets a new random id,
/// once, saved through the settings writer before it takes effect; new seeds and migrations get random ids.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class StationRekeyTests : IDisposable
{
    private const string FormerKexp = "382c3ae96fcf455fb90c352050471d38";
    private const string Own = "0f8fad5bd9cb469fa16570867728950e";

    // A 0.5 test build's file: the migrated "live", a starter with its former fixed id, and the admin's own station.
    private const string PublicIds = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <LibraryName>Live Radio</LibraryName>
          <Stations>
            <StationDefinition>
              <Id>live</Id>
              <Name>IHateRadio</Name>
              <Kind>SubWave</Kind>
              <Enabled>true</Enabled>
              <BaseUrl>http://radio.example:7700</BaseUrl>
              <StreamPath>/stream.mp3</StreamPath>
            </StationDefinition>
            <StationDefinition>
              <Id>382c3ae96fcf455fb90c352050471d38</Id>
              <Name>KEXP</Name>
              <Kind>Stream</Kind>
              <Enabled>true</Enabled>
              <SortOrder>1</SortOrder>
              <StreamUrl>https://kexp.streamguys1.com/kexp160.aac</StreamUrl>
              <Codec>aac</Codec>
              <Container>aac</Container>
            </StationDefinition>
            <StationDefinition>
              <Id>0f8fad5bd9cb469fa16570867728950e</Id>
              <Name>Mine</Name>
              <Kind>Stream</Kind>
              <Enabled>true</Enabled>
              <SortOrder>2</SortOrder>
              <StreamUrl>https://stream.example.org/mp3</StreamUrl>
            </StationDefinition>
          </Stations>
          <PreviousChannelNames />
          <CarriedChannelNames />
          <SchemaVersion>1</SchemaVersion>
          <NowPlayingEnabled>true</NowPlayingEnabled>
          <LastChangedUtc>2026-10-01T00:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellywave-r2a-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibrary _library = FakeLibrary.Create();
    private readonly FileXmlSerializer _xml = new();

    public StationRekeyTests()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    }

    private string ConfigDir => Path.Combine(_root, "configurations");

    private string ConfigFile => Path.Combine(ConfigDir, Path.ChangeExtension(Path.GetFileName(typeof(Plugin).Assembly.Location), ".xml"));

    public void Dispose()
    {
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Rekey_replaces_only_the_public_ids_and_a_rekeyed_list_is_left_alone()
    {
        var config = new PluginConfiguration
        {
            Stations =
            {
                new StationDefinition { Id = "live" },
                new StationDefinition { Id = FormerKexp },
                new StationDefinition { Id = Own },
                null!,
                new StationDefinition { Id = null! },
                new StationDefinition { Id = "LIVE" }   // ids compare ordinally: not a known public id
            }
        };

        Assert.True(StationIds.Rekey(config));

        var ids = config.Stations.Select(s => s?.Id).ToArray();
        Assert.All(ids.Take(2), id => Assert.True(Guid.TryParseExact(id, "N", out _), id));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(new[] { Own, null, null, "LIVE" }, ids.Skip(2));
        Assert.DoesNotContain(ids, id => id is not null && StationIds.KnownPublicIds.Contains(id));

        var again = ids.ToArray();
        Assert.False(StationIds.Rekey(config));
        Assert.Equal(again, config.Stations.Select(s => s?.Id));
    }

    [Fact]
    public void Two_stations_with_one_public_id_stay_one_duplicate()
    {
        // The second of a duplicate id is refused as a duplicate; giving each its own id would list it.
        var config = new PluginConfiguration { Stations = { new StationDefinition { Id = "live" }, new StationDefinition { Id = "live" } } };

        Assert.True(StationIds.Rekey(config));

        Assert.Equal(config.Stations[0].Id, config.Stations[1].Id);
        Assert.NotEqual("live", config.Stations[0].Id);
    }

    [Fact]
    public void The_known_public_ids_are_live_and_the_five_former_starter_ids()
    {
        Assert.Equal(6, StationIds.KnownPublicIds.Count);
        Assert.Contains(ConfigMigration.LegacyMigratedStationId, StationIds.KnownPublicIds);
        Assert.Contains(FormerKexp, StationIds.KnownPublicIds);
        Assert.DoesNotContain(StarterStations.Create(), s => StationIds.KnownPublicIds.Contains(s.Id));
    }

    [Fact]
    public void A_start_rekeys_the_public_ids_once_saves_them_and_a_restart_keeps_them()
    {
        File.WriteAllText(ConfigFile, PublicIds);
        var before = JellywaveChannel.ComputeDataVersion(OnDisk());

        var plugin = NewPlugin();

        var ids = plugin.Configuration.Stations.Select(s => s.Id).ToArray();
        Assert.All(ids.Take(2), id => Assert.True(Guid.TryParseExact(id, "N", out _), id));
        Assert.Equal(Own, ids[2]);
        Assert.Equal(ids, OnDisk().Stations.Select(s => s.Id));
        Assert.Equal(1, _xml.Saves);
        Assert.Equal(PublicIds, File.ReadAllText(ConfigFile + ConfigurationFile.PreviousSuffix));   // the old file is the one previous copy
        Assert.True(plugin.Configuration.LastChangedUtc > new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        // DataVersion changes once: the re-key lists new items, a restart lists the same ones.
        var after = JellywaveChannel.ComputeDataVersion(plugin.Configuration);
        Assert.NotEqual(before, after);

        var restarted = NewPlugin();
        Assert.Equal(ids, restarted.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(1, _xml.Saves);
        Assert.Equal(after, JellywaveChannel.ComputeDataVersion(restarted.Configuration));
    }

    [Fact]
    public void A_failed_rekey_save_changes_nothing_and_the_next_start_tries_again()
    {
        // Card R1: written before it takes effect. Memory and file keep the old ids, so the items stay what they were.
        File.WriteAllText(ConfigFile, PublicIds);
        _xml.FailSave = true;

        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(new[] { "live", FormerKexp, Own }, plugin.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(PublicIds, File.ReadAllText(ConfigFile));
        Assert.False(plugin.RunningOnDefaults || plugin.RunningUnsaved || plugin.PreservationFailed);

        _xml.FailSave = false;
        var restarted = NewPlugin();

        Assert.DoesNotContain(restarted.Configuration.Stations, s => StationIds.KnownPublicIds.Contains(s.Id));
        Assert.Equal(restarted.Configuration.Stations.Select(s => s.Id), OnDisk().Stations.Select(s => s.Id));
    }

    [Fact]
    public void A_list_without_public_ids_is_not_saved_at_start()
    {
        File.WriteAllText(ConfigFile, PublicIds.Replace("<Id>live</Id>", "<Id>5d1e0c0b9a7f4e3c8b2a1f0e9d8c7b6a</Id>", StringComparison.Ordinal)
            .Replace("<Id>" + FormerKexp + "</Id>", "<Id>a1b2c3d4e5f60718293a4b5c6d7e8f90</Id>", StringComparison.Ordinal));

        NewPlugin();

        Assert.Equal(0, _xml.Saves);
    }

    [Fact]
    public void A_page_save_with_a_public_id_is_rekeyed_before_it_is_written()
    {
        // A configuration page opened before the upgrade restart still holds "live"; its save must not write it back.
        File.WriteAllText(ConfigFile, PublicIds);
        var plugin = NewPlugin();
        var posted = new PluginConfiguration
        {
            SchemaVersion = PluginConfiguration.CurrentSchemaVersion,
            Stations =
            {
                new StationDefinition { Id = "live", Name = "IHateRadio", Kind = StationKind.SubWave, BaseUrl = "http://radio.example:7700" },
                new StationDefinition { Id = Own, Name = "Mine", Kind = StationKind.Stream, StreamUrl = "https://stream.example.org/mp3" }
            }
        };

        plugin.UpdateConfiguration(posted);

        var ids = plugin.Configuration.Stations.Select(s => s.Id).ToArray();
        Assert.True(Guid.TryParseExact(ids[0], "N", out _), ids[0]);
        Assert.Equal(Own, ids[1]);
        Assert.Equal(ids, OnDisk().Stations.Select(s => s.Id));
        Assert.DoesNotContain("<Id>live</Id>", File.ReadAllText(ConfigFile), StringComparison.Ordinal);
    }

    private Plugin NewPlugin()
    {
        var paths = (PluginWiringTests.Paths)(object)DispatchProxy.Create<IApplicationPaths, PluginWiringTests.Paths>();
        paths.Root = _root;
        return new Plugin((IApplicationPaths)(object)paths, _xml, _library.Manager, NullLogger<Plugin>.Instance);
    }

    private PluginConfiguration OnDisk()
    {
        using var stream = File.OpenRead(ConfigFile);
        using var reader = XmlReader.Create(stream);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }
}
