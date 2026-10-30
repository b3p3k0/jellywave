using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L1 review (R1, S1): the production wiring of the rename bridge. A real <see cref="Plugin"/> is built on a temporary
/// configuration folder with an <c>XmlSerializer</c>-backed <see cref="IXmlSerializer"/> and Jellyfin 12.3's id rule
/// (<see cref="FakeLibrary"/>): its constructor migrates and saves a 0.4.x file, <c>UpdateConfiguration</c> keeps and
/// records tile names, and the public DI constructors of <see cref="StationAccess"/>, <see cref="JellywaveChannel"/> and
/// <see cref="AccessGranter"/> read <see cref="Plugin.Instance"/>.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class PluginWiringTests : IDisposable
{
    private const string OldTile = "IHateRadio";

    private const string Configured042 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
          <StreamPath>/stream.mp3</StreamPath>
          <StationName>IHateRadio</StationName>
          <Delivery>Relay</Delivery>
          <StreamBitrateKbps>192</StreamBitrateKbps>
        </PluginConfiguration>
        """;

    // Card L9: a 0.4.x install that never had a station URL (0.4.x wrote its defaults on first start).
    private const string Unconfigured042 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl />
          <StreamPath>/stream.mp3</StreamPath>
          <ClientStreamUrl />
          <StationName>SUB/WAVE</StationName>
          <StationTagline />
          <Delivery>Relay</Delivery>
          <NowPlayingEnabled>true</NowPlayingEnabled>
          <StreamBitrateKbps>192</StreamBitrateKbps>
          <LastChangedUtc>1970-01-01T00:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    // Card L9: a 0.5 file whose admin removed every station.
    private const string AllRemoved = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <LibraryName>Live Radio</LibraryName>
          <Stations />
          <PreviousChannelNames>
            <string>SUB/WAVE</string>
          </PreviousChannelNames>
          <CarriedChannelNames />
          <SchemaVersion>1</SchemaVersion>
          <StationBaseUrl />
          <StreamPath>/stream.mp3</StreamPath>
          <StationName>SUB/WAVE</StationName>
          <StationTagline />
          <NowPlayingEnabled>true</NowPlayingEnabled>
          <CoverArtEnabled>true</CoverArtEnabled>
          <NowPlayingPollSeconds>20</NowPlayingPollSeconds>
          <StreamBitrateKbps>192</StreamBitrateKbps>
          <StreamSampleRate>44100</StreamSampleRate>
          <StreamChannels>2</StreamChannels>
          <LastChangedUtc>2026-10-29T12:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    // Card L9: a 0.5 file that kept one starter (XCSB) and removed the others.
    private const string OneStarterLeft = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <LibraryName>Live Radio</LibraryName>
          <Stations>
            <StationDefinition>
              <Id>172b37d4d9c547d386b242b7f937e2f5</Id>
              <Name>XCSB</Name>
              <Kind>Stream</Kind>
              <Enabled>true</Enabled>
              <SortOrder>0</SortOrder>
              <BaseUrl />
              <StreamPath>/stream.mp3</StreamPath>
              <StreamUrl>https://xcsb.live/listen/xcsb-cleveland/xcsb160.mp3</StreamUrl>
              <LogoUrl>https://xcsb.live/static/uploads/browser_icon/original.png</LogoUrl>
              <Tagline />
              <Genre>Freeform</Genre>
              <Notes>Cleveland's Alternative to FM.</Notes>
              <Website>https://xcsb.live/</Website>
              <Codec>mp3</Codec>
              <Container>mp3</Container>
              <BitrateKbps>160</BitrateKbps>
              <SampleRate>44100</SampleRate>
              <Channels>2</Channels>
            </StationDefinition>
          </Stations>
          <PreviousChannelNames>
            <string>SUB/WAVE</string>
          </PreviousChannelNames>
          <CarriedChannelNames />
          <SchemaVersion>1</SchemaVersion>
        </PluginConfiguration>
        """;

    // Jellyfin's PluginsController deserializes the page's POST with System.Text.Json (string enums, PascalCase).
    private static readonly JsonSerializerOptions PageJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellywave-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibrary _library = FakeLibrary.Create();
    private readonly FileXmlSerializer _xml = new();

    public PluginWiringTests()
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

    /// <summary>The two <see cref="IApplicationPaths"/> members <c>BasePlugin</c> reads; anything else throws.</summary>
    public class Paths : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_" + nameof(IApplicationPaths.PluginsPath) => Path.Combine(Root, "plugins"),
                "get_" + nameof(IApplicationPaths.PluginConfigurationsPath) => Path.Combine(Root, "configurations"),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public List<Guid> Saved { get; } = new();

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user)
        {
            Saved.Add(user.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class Locator(Guid id) : IChannelLocator
    {
        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
            => Task.FromResult(ChannelResolution.Found(new Channel { Id = id }));
    }

    private Guid Id(string name) => _library.IdFor("Channel " + name, typeof(Channel));

    private Plugin NewPlugin(FakeLibrary? library = null)
    {
        var paths = (Paths)(object)DispatchProxy.Create<IApplicationPaths, Paths>();
        paths.Root = _root;
        return new Plugin((IApplicationPaths)(object)paths, _xml, (library ?? _library).Manager, NullLogger<Plugin>.Instance);
    }

    private PluginConfiguration OnDisk()
    {
        using var stream = File.OpenRead(ConfigFile);
        using var reader = XmlReader.Create(stream);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }

    private static User NewUser(string name, bool allChannels = false, Guid[]? enabled = null, Guid[]? blocked = null)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        if (enabled is not null)
        {
            u.SetPreference(PreferenceKind.EnabledChannels, enabled);
        }

        if (blocked is not null)
        {
            u.SetPreference(PreferenceKind.BlockedChannels, blocked);
        }

        return u;
    }

    private static ClaimsPrincipal Caller(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(StationAccess.ApiKeyClaim, "False"),
                new Claim(StationAccess.UserIdClaim, userId.ToString("N", CultureInfo.InvariantCulture))
            ],
            "Test"));

    [Fact]
    public void The_constructor_migrates_and_saves_a_042_file_before_publishing_the_instance()
    {
        File.WriteAllText(ConfigFile, Configured042);

        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(1, plugin.Configuration.SchemaVersion);
        Assert.Equal("Live Radio", plugin.Configuration.LibraryName);
        Assert.Equal(new[] { OldTile }, plugin.Configuration.PreviousChannelNames);

        var saved = OnDisk();
        Assert.Equal(1, saved.SchemaVersion);
        var station = Assert.Single(saved.Stations);
        Assert.Equal(Assert.Single(plugin.Configuration.Stations).Id, station.Id);
        Assert.True(Guid.TryParseExact(station.Id, "N", out _), station.Id);   // card R2a: random, never "live"
        Assert.Equal(new[] { OldTile }, saved.PreviousChannelNames);
        Assert.Equal("http://radio.example:7700", saved.StationBaseUrl);   // kept for a downgrade
    }

    [Fact]
    public void A_current_file_is_not_saved_again()
    {
        File.WriteAllText(ConfigFile, Configured042);
        NewPlugin();
        var saves = _xml.Saves;

        NewPlugin();

        Assert.Equal(saves, _xml.Saves);
    }

    // Card R2a: every seed gets new random ids, so a fresh install is recognised by the starters' names, and "the same
    // stations" by their ids against the first load.
    private static string[] StarterNames => StarterStations.Create().Select(s => s.Name).ToArray();

    private int Occurrences(string text) => File.ReadAllText(ConfigFile).Split(text).Length - 1;

    // What the page posts back: the configuration it read, through System.Text.Json, with the admin's change applied.
    private static PluginConfiguration PageCopy(PluginConfiguration config, Func<StationDefinition, bool> keep)
    {
        var copy = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config, PageJson), PageJson)!;
        copy.Stations = new System.Collections.ObjectModel.Collection<StationDefinition>(copy.Stations.Where(keep).ToList());
        return copy;
    }

    [Fact]
    public void A_fresh_install_writes_the_starters_into_the_first_file()
    {
        // Card L9: no file at all is a fresh install.
        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.False(plugin.RunningOnDefaults);
        Assert.Equal(StarterNames, plugin.Configuration.Stations.Select(s => s.Name));
        Assert.Equal(1, plugin.Configuration.SchemaVersion);
        Assert.Equal("Live Radio", plugin.Configuration.LibraryName);
        Assert.Equal(1, _xml.Saves);

        var ids = plugin.Configuration.Stations.Select(s => s.Id).ToArray();
        var saved = OnDisk();
        Assert.Equal(ids, saved.Stations.Select(s => s.Id));
        Assert.Equal(1, saved.SchemaVersion);
        Assert.All(ids, id => Assert.Equal(1, Occurrences("<Id>" + id + "</Id>")));
        Assert.DoesNotContain(ids, StationIds.KnownPublicIds.Contains);
    }

    [Fact]
    public void The_starters_load_once_after_a_restart()
    {
        // The XmlSerializer trap: a list filled before loading would come back with every saved entry appended. Card R2a:
        // a restart keeps the random ids the seed saved (no re-key, no save).
        var ids = NewPlugin().Configuration.Stations.Select(s => s.Id).ToArray();
        var saves = _xml.Saves;

        var restarted = NewPlugin();
        var again = NewPlugin();

        Assert.Equal(ids, restarted.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(ids, again.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(saves, _xml.Saves);
        Assert.All(ids, id => Assert.Equal(1, Occurrences("<Id>" + id + "</Id>")));
    }

    [Fact]
    public void A_list_the_admin_empties_stays_empty_across_restarts()
    {
        var plugin = NewPlugin();
        plugin.UpdateConfiguration(PageCopy(plugin.Configuration, _ => false));
        Assert.Empty(OnDisk().Stations);
        var saves = _xml.Saves;

        Assert.Empty(NewPlugin().Configuration.Stations);
        Assert.Empty(NewPlugin().Configuration.Stations);
        Assert.Empty(OnDisk().Stations);
        Assert.Equal(saves, _xml.Saves);
    }

    [Fact]
    public void A_saved_file_with_no_stations_gets_none()
    {
        File.WriteAllText(ConfigFile, AllRemoved);

        var plugin = NewPlugin();

        Assert.Empty(plugin.Configuration.Stations);
        Assert.Equal(0, _xml.Saves);
        Assert.Equal(AllRemoved, File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void A_removed_starter_is_never_put_back()
    {
        var plugin = NewPlugin();
        plugin.UpdateConfiguration(PageCopy(plugin.Configuration, s => s.Name != "KEXP"));

        var restarted = NewPlugin();

        var expected = plugin.Configuration.Stations.Select(s => s.Id).ToArray();
        Assert.Equal(StarterNames.Where(n => n != "KEXP"), restarted.Configuration.Stations.Select(s => s.Name));
        Assert.Equal(expected, restarted.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(expected, OnDisk().Stations.Select(s => s.Id));
        Assert.Equal(0, Occurrences("kexp"));
    }

    [Fact]
    public void A_saved_file_with_one_starter_keeps_just_that_one()
    {
        File.WriteAllText(ConfigFile, OneStarterLeft);

        var plugin = NewPlugin();

        var station = Assert.Single(plugin.Configuration.Stations);
        Assert.Equal(("XCSB", "Freeform", "https://xcsb.live/"), (station.Name, station.Genre, station.Website));

        // Card R2a: the file still has the starter's former fixed id, so the start gave it a random one and saved once.
        Assert.NotEqual("172b37d4d9c547d386b242b7f937e2f5", station.Id);
        Assert.Equal(1, _xml.Saves);
        Assert.Equal(station.Id, Assert.Single(OnDisk().Stations).Id);
    }

    [Fact]
    public void An_upgrade_from_042_gets_no_starters()
    {
        File.WriteAllText(ConfigFile, Configured042);

        var id = Assert.Single(NewPlugin().Configuration.Stations).Id;
        Assert.Equal(id, Assert.Single(OnDisk().Stations).Id);
        Assert.Equal(StationKind.SubWave, Assert.Single(OnDisk().Stations).Kind);
        Assert.DoesNotContain(OnDisk().Stations, s => StarterNames.Contains(s.Name));
    }

    [Fact]
    public void An_unconfigured_042_install_gets_no_starters()
    {
        // 0.4.x wrote its defaults on first start, so a file is there: an upgrade, not a fresh install.
        File.WriteAllText(ConfigFile, Unconfigured042);

        var plugin = NewPlugin();

        Assert.Empty(plugin.Configuration.Stations);
        Assert.Equal(1, OnDisk().SchemaVersion);
        Assert.Empty(OnDisk().Stations);
        Assert.Empty(NewPlugin().Configuration.Stations);
    }

    [Fact]
    public void A_fresh_install_whose_save_fails_keeps_the_starters_in_memory_and_seeds_again_next_start()
    {
        _xml.FailSave = true;

        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.True(plugin.RunningUnsaved);
        Assert.False(plugin.RunningOnDefaults);   // nothing failed to load: there was no file
        Assert.Equal(StarterNames, plugin.Configuration.Stations.Select(s => s.Name));
        Assert.False(File.Exists(ConfigFile));

        // Still no file, so the next start is a fresh install again; this time the save works.
        _xml.FailSave = false;
        var restarted = NewPlugin();

        Assert.False(restarted.RunningUnsaved);
        Assert.False(restarted.RunningOnDefaults);
        Assert.Equal(StarterNames, OnDisk().Stations.Select(s => s.Name));
        Assert.Equal(restarted.Configuration.Stations.Select(s => s.Id), OnDisk().Stations.Select(s => s.Id));
    }

    [Fact]
    public void A_fresh_install_whose_save_fails_warns_that_it_could_not_save_until_a_page_save_works()
    {
        // L9 fix: the load-failure warning ("could not be loaded", "blocks on the old tile name") was wrong here.
        _xml.FailSave = true;
        var plugin = NewPlugin();

        var health = Health();
        Assert.Equal(Api.JellywaveController.UnsavedWarning, health.ConfigurationWarning);
        Assert.True(health.ConfigurationUnsaved);
        Assert.DoesNotContain("loaded", health.ConfigurationWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("tile", health.ConfigurationWarning, StringComparison.Ordinal);
        Assert.Contains("starter stations", health.ConfigurationWarning, StringComparison.Ordinal);

        // Carried names wait for the page save, as on defaults; a failed page save leaves the warning up.
        _xml.FailSave = false;
        var saves = _xml.Saves;
        plugin.RecordCarriedNames(new[] { "SUB/WAVE" });
        Assert.Equal(saves, _xml.Saves);
        Assert.False(File.Exists(ConfigFile));
        Assert.Equal(new[] { "SUB/WAVE" }, plugin.Configuration.CarriedChannelNames);

        _xml.FailSave = true;
        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(PageCopy(plugin.Configuration, _ => true)));
        Assert.True(plugin.RunningUnsaved);
        Assert.Equal(Api.JellywaveController.UnsavedWarning, Health().ConfigurationWarning);

        _xml.FailSave = false;
        plugin.UpdateConfiguration(PageCopy(plugin.Configuration, _ => true));

        Assert.False(plugin.RunningUnsaved);
        Assert.Null(Health().ConfigurationWarning);
        Assert.False(Health().ConfigurationUnsaved);
        Assert.Equal(plugin.Configuration.Stations.Select(s => s.Id), OnDisk().Stations.Select(s => s.Id));
        Assert.Equal(StarterNames, OnDisk().Stations.Select(s => s.Name));
    }

    [Fact]
    public void A_fresh_install_with_a_failing_id_rule_saves_the_starters_unmigrated_and_migrates_them_next_start()
    {
        var broken = FakeLibrary.Create();
        broken.ThrowOnIds = true;

        var plugin = NewPlugin(broken);

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(0, OnDisk().SchemaVersion);
        var ids = plugin.Configuration.Stations.Select(s => s.Id).ToArray();
        Assert.Equal(ids, OnDisk().Stations.Select(s => s.Id));

        var restarted = NewPlugin();

        Assert.Equal(1, restarted.Configuration.SchemaVersion);
        Assert.Equal(ids, restarted.Configuration.Stations.Select(s => s.Id));
        Assert.Equal(ids, OnDisk().Stations.Select(s => s.Id));
    }

    [Fact]
    public void Update_configuration_records_the_outgoing_name_and_keeps_the_earlier_ones()
    {
        File.WriteAllText(ConfigFile, Configured042);
        var plugin = NewPlugin();

        // A body without the list (an API client, or a page that dropped it) renames the tile.
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SchemaVersion = 1,
            LibraryName = "Kevin Radio",
            PreviousChannelNames = null!,
            Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700" } }
        });

        Assert.Equal(new[] { OldTile, "Live Radio" }, plugin.Configuration.PreviousChannelNames);
        Assert.Equal(new[] { OldTile, "Live Radio" }, OnDisk().PreviousChannelNames);
        Assert.Equal("Kevin Radio", OnDisk().LibraryName);
    }

    [Fact]
    public void A_station_table_save_round_trips_the_whole_list_and_still_merges_the_bridge_names()
    {
        // Card L3: the page posts every station (its order as SortOrder, new GUID ids, kinds, logo URLs) and may drop the
        // bridge lists; Jellyfin deserializes the POST with System.Text.Json (string enums) and calls UpdateConfiguration.
        File.WriteAllText(ConfigFile, Configured042);
        var plugin = NewPlugin();
        const string Posted = """
            {
              "SchemaVersion": 1,
              "LibraryName": "Kevin Radio",
              "PreviousChannelNames": [],
              "NowPlayingEnabled": true,
              "NowPlayingPollSeconds": 30,
              "CoverArtEnabled": false,
              "Stations": [
                { "Id": "0f8fad5bd9cb469fa16570867728950e", "Name": "Radio Paradise", "Kind": "Stream", "Enabled": true, "SortOrder": 0,
                  "StreamUrl": "https://stream.example.org/mp3-192?src=jellywave", "LogoUrl": "https://img.example.org/logo.png?size=512",
                  "Delivery": "Direct", "ClientStreamUrl": "", "Codec": "mp3", "Container": "mp3", "BitrateKbps": 192, "SampleRate": 44100, "Channels": 2 },
                { "Id": "5d1e0c0b9a7f4e3c8b2a1f0e9d8c7b6a", "Name": "IHateRadio", "Kind": "SubWave", "Enabled": true, "SortOrder": 1, "BaseUrl": "http://radio.example:7700",
                  "StreamPath": "/stream.mp3", "Tagline": "late-night static", "Delivery": "Relay", "Codec": "mp3", "Container": "mp3",
                  "BitrateKbps": 192, "SampleRate": 44100, "Channels": 2 },
                { "Id": "7c9e6679742540de944be07fc1f90ae7", "Name": "Station Two", "Kind": "SubWave", "Enabled": false, "SortOrder": 2,
                  "BaseUrl": "https://two.example.org/radio", "StreamPath": "/live.aac", "Delivery": "Relay", "Codec": "aac", "Container": "aac",
                  "BitrateKbps": 128, "SampleRate": 48000, "Channels": 2 }
              ]
            }
            """;
        plugin.UpdateConfiguration(JsonSerializer.Deserialize<PluginConfiguration>(Posted, PageJson)!);

        foreach (var config in new[] { plugin.Configuration, OnDisk(), NewPlugin().Configuration })
        {
            Assert.Equal(
                new[]
                {
                    "0f8fad5bd9cb469fa16570867728950e|Stream|0|True|https://stream.example.org/mp3-192?src=jellywave|https://img.example.org/logo.png?size=512",
                    "5d1e0c0b9a7f4e3c8b2a1f0e9d8c7b6a|SubWave|1|True|http://radio.example:7700/stream.mp3|",
                    "7c9e6679742540de944be07fc1f90ae7|SubWave|2|False|https://two.example.org/radio/live.aac|"
                },
                config.Stations.Select(s => string.Join('|', s.Id, s.Kind, s.SortOrder, s.Enabled, s.Kind == StationKind.Stream ? s.StreamUrl : s.BaseUrl + s.StreamPath, s.LogoUrl)));
            Assert.Equal(("aac", 128, 48000), (config.Stations[2].Codec, config.Stations[2].BitrateKbps, config.Stations[2].SampleRate));
            Assert.Equal("late-night static", config.Stations[1].Tagline);
            Assert.Equal(new[] { OldTile, "Live Radio" }, config.PreviousChannelNames);
            Assert.Equal("Kevin Radio", config.LibraryName);
            Assert.Equal((true, 30, false), (config.NowPlayingEnabled, config.NowPlayingPollSeconds, config.CoverArtEnabled));
        }
    }

    [Fact]
    public async Task The_di_constructors_read_the_bridge_through_the_plugin_instance()
    {
        await File.WriteAllTextAsync(ConfigFile, Configured042);
        var plugin = NewPlugin();
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SchemaVersion = 1,
            LibraryName = "Kevin Radio",
            PreviousChannelNames = { OldTile },
            Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700" } }
        });
        var currentId = Id("Kevin Radio");
        _library.Items[currentId] = new Channel { Id = currentId, Name = "Kevin Radio" };

        var oldBlocked = NewUser("oldBlocked", blocked: [Id(OldTile)]);
        var everyone = NewUser("everyone", allChannels: true);
        var byName = NewUser("byName", enabled: [Id(OldTile)]);
        var store = new FakeStore();
        foreach (var u in new[] { oldBlocked, everyone, byName })
        {
            store.Users[u.Id] = u;
        }

        var finder = new LiveItemFinder(_library.Manager, () => [], NullLogger<LiveItemFinder>.Instance);
        var bridge = new RenameBridge(finder);

        // StationAccess (DI constructor): the old block refuses, Jellyfin's rule still passes everyone else.
        var access = new StationAccess(store, finder, bridge, NullLogger<StationAccess>.Instance);
        Assert.Equal(StationAccessReason.PreviousChannelBlocked, access.Check(Caller(oldBlocked.Id)).Reason);
        Assert.Equal(StationAccessReason.Allowed, access.Check(Caller(everyone.Id)).Reason);

        // The channel (DI constructor): named by the plugin's tile name, hidden from the old-blocked user.
        var channel = new JellywaveChannel(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), store, bridge);
        Assert.Equal("Kevin Radio", channel.Name);
        Assert.False(channel.IsEnabledFor(oldBlocked.Id.ToString("N", CultureInfo.InvariantCulture)));
        Assert.True(channel.IsEnabledFor(everyone.Id.ToString("N", CultureInfo.InvariantCulture)));

        // The granter (DI constructor): carries over and records the carried names on the plugin, saved.
        using var granter = new AccessGranter(store, new Locator(currentId), bridge, NullLogger<AccessGranter>.Instance);
        var result = await granter.CarryOverAsync(CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(1, result.CarriedGrants);
        Assert.Equal(1, result.CarriedBlocks);
        Assert.Contains(currentId, byName.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
        Assert.Contains(currentId, oldBlocked.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels));
        Assert.Equal(new[] { OldTile, "Live Radio" }, plugin.Configuration.CarriedChannelNames);
        Assert.Equal(new[] { OldTile, "Live Radio" }, OnDisk().CarriedChannelNames);
        Assert.Equal(0, granter.CarryOverStatus().Pending);

        // A later page save that does not send the carried names keeps them.
        plugin.UpdateConfiguration(new PluginConfiguration { SchemaVersion = 1, LibraryName = "Kevin Radio", CarriedChannelNames = null! });
        Assert.Equal(new[] { OldTile, "Live Radio" }, plugin.Configuration.CarriedChannelNames);
    }

    [Fact]
    public void The_station_resolver_di_constructor_reads_the_plugin_instance()
    {
        // Card L5: the routes' station comes from the configuration the plugin holds, through the resolver's DI constructor.
        var plugin = NewPlugin();
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SchemaVersion = 1,
            Stations =
            {
                new StationDefinition { Id = "one", BaseUrl = "http://radio.example:7700" },
                new StationDefinition { Id = "two", BaseUrl = "http://two.example:7700", SortOrder = 1 },
                new StationDefinition { Id = "plain", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/p.mp3", SortOrder = 2 }
            }
        });

        var resolver = new StationResolver(new RequestReceipts(), NullLogger<StationResolver>.Instance);

        Assert.Equal(new[] { "one", "two" }, resolver.SubWaveStations().Select(s => s.Station.Id));
        Assert.Equal(new Uri("http://two.example:7700/"), resolver.Resolve("two")!.BaseUrl);
        Assert.Equal("one", resolver.Resolve(null)!.Station.Id);
        Assert.Null(resolver.Resolve("plain"));
    }

    [Fact]
    public void A_failed_save_keeps_the_migrated_configuration_in_memory()
    {
        File.WriteAllText(ConfigFile, Configured042);
        _xml.FailSave = true;

        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(1, plugin.Configuration.SchemaVersion);
        Assert.Equal(0, OnDisk().SchemaVersion);   // the file is as it was; the next start migrates again
    }

    [Fact]
    public void An_unloadable_configuration_does_not_stop_the_plugin()
    {
        // S1: BasePlugin falls back to defaults and saves them; when that save fails too the getter throws.
        File.WriteAllText(ConfigFile, Configured042);
        _xml.FailLoad = true;
        _xml.FailSave = true;

        var plugin = NewPlugin();

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(0, plugin.Configuration.SchemaVersion);   // defaults, unmigrated, in memory
        Assert.Empty(plugin.Configuration.Stations);
    }

    [Fact]
    public void Running_on_defaults_is_reported_until_the_next_save()
    {
        // F-S1: after a failed load the plugin cannot know the real old tile name; the page must say so.
        File.WriteAllText(ConfigFile, Configured042);
        _xml.FailLoad = true;
        _xml.FailSave = true;
        var plugin = NewPlugin();
        Assert.True(plugin.RunningOnDefaults);
        Assert.False(plugin.RunningUnsaved);
        Assert.Equal(Api.JellywaveController.DefaultsWarning, Health().ConfigurationWarning);
        Assert.False(Health().ConfigurationUnsaved);

        // A save that fails leaves the warning up (RA L1 S-1).
        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(new PluginConfiguration { SchemaVersion = 1 }));
        Assert.True(plugin.RunningOnDefaults);
        Assert.Equal(Api.JellywaveController.DefaultsWarning, Health().ConfigurationWarning);

        _xml.FailSave = false;
        plugin.UpdateConfiguration(new PluginConfiguration { SchemaVersion = 1 });

        Assert.False(plugin.RunningOnDefaults);
        Assert.Null(Health().ConfigurationWarning);
    }

    [Fact]
    public void Carried_names_on_defaults_stay_in_memory_and_do_not_overwrite_the_unreadable_file()
    {
        // RA L1 S-2: the disk became writable again, but the file still holds the admin's real configuration.
        File.WriteAllText(ConfigFile, Configured042);
        _xml.FailLoad = true;
        _xml.FailSave = true;
        var plugin = NewPlugin();
        _xml.FailSave = false;
        var saves = _xml.Saves;

        plugin.RecordCarriedNames(new[] { "SUB/WAVE" });

        Assert.Equal(saves, _xml.Saves);
        Assert.Equal(Configured042, File.ReadAllText(ConfigFile));
        Assert.Equal(new[] { "SUB/WAVE" }, plugin.Configuration.CarriedChannelNames);
    }

    [Fact]
    public void A_migrated_load_has_no_configuration_warning()
    {
        File.WriteAllText(ConfigFile, Configured042);
        Assert.False(NewPlugin().RunningOnDefaults);
        Assert.Null(Health().ConfigurationWarning);
    }

    [Fact]
    public void A_page_save_after_a_failed_migration_keeps_the_old_tile_as_an_earlier_name()
    {
        // F-R1: the in-memory configuration is unmigrated (schema 0); the page posts it back with Stations[0].
        File.WriteAllText(ConfigFile, Configured042);
        var broken = FakeLibrary.Create();
        broken.ThrowOnIds = true;
        var plugin = NewPlugin(broken);
        broken.ThrowOnIds = false;   // the id rule works again by the time the admin saves

        var posted = new PluginConfiguration { StationName = OldTile, StationBaseUrl = "http://radio.example:7700" };
        posted.Stations.Add(new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700" });
        plugin.UpdateConfiguration(posted);

        Assert.Equal(1, plugin.Configuration.SchemaVersion);
        Assert.Equal(new[] { OldTile }, plugin.Configuration.PreviousChannelNames);
        Assert.Equal(new[] { Id(OldTile) }, new RenameBridge(Id).PreviousIds(plugin.Configuration));
        Assert.Equal(new[] { OldTile }, OnDisk().PreviousChannelNames);
    }

    [Fact]
    public async Task A_carried_record_during_a_page_save_waits_for_it_and_is_kept()
    {
        // F-S2 (R02, R03, R09): UpdateConfiguration and RecordCarriedNames share the save gate, and the record reads the
        // configuration inside it. The page save is held inside PrepareSave (at the id request for the already carried
        // name), while the old configuration is still in effect.
        var start = new PluginConfiguration
        {
            SchemaVersion = 1,
            PreviousChannelNames = { OldTile, "Gone" },
            CarriedChannelNames = { "Gone" },
            Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700" } }
        };
        _xml.SerializeToFile(start, ConfigFile);
        var plugin = NewPlugin();
        _library.HoldKey = "Channel Gone";

        var save = Task.Run(() => plugin.UpdateConfiguration(new PluginConfiguration
        {
            SchemaVersion = 1,
            LibraryName = "Kevin Radio",
            PreviousChannelNames = { OldTile, "Gone" },
            CarriedChannelNames = null!,
            Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700" } }
        }));
        Assert.True(_library.Holding.Wait(TimeSpan.FromSeconds(10)));

        var record = Task.Run(() => plugin.RecordCarriedNames([OldTile]));
        await Task.WhenAny(record, Task.Delay(500));
        Assert.False(record.IsCompleted, "RecordCarriedNames ran while a page save held the save gate");

        _library.Resume.Set();
        await save;
        await record;

        Assert.Equal("Kevin Radio", plugin.Configuration.LibraryName);
        Assert.Equal(new[] { "Gone", OldTile }, plugin.Configuration.CarriedChannelNames);
        Assert.Equal("Kevin Radio", OnDisk().LibraryName);
        Assert.Equal(new[] { "Gone", OldTile }, OnDisk().CarriedChannelNames);
    }

    private Api.HealthResponse Health()
    {
        var station = new StationClient(null!);
        return new Api.JellywaveController(
            station,
            null!,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            new LiveItemFinder(_library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<Api.JellywaveController>.Instance,
            () => Plugin.Instance?.Configuration).Health().Value!;
    }

    [Fact]
    public void A_failing_migration_runs_the_file_unmigrated_and_still_hides_from_old_blocks()
    {
        // S1: the id rule fails while migrating; the plugin starts with the file as loaded.
        File.WriteAllText(ConfigFile, Configured042);
        var broken = FakeLibrary.Create();
        broken.ThrowOnIds = true;

        var plugin = NewPlugin(broken);

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal(0, plugin.Configuration.SchemaVersion);
        Assert.Equal(OldTile, plugin.Configuration.StationName);
        Assert.Empty(plugin.Configuration.PreviousChannelNames);
        Assert.Equal(0, _xml.Saves);
        Assert.Equal(new[] { Id(OldTile) }, new RenameBridge(Id).PreviousIds(plugin.Configuration));
    }
}
