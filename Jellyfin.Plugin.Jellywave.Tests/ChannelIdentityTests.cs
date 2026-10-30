using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15 (D1): the channel entity is loaded by the id Jellyfin derives from the name, never by a name scan, and a
/// name that another plugin's channel also derives is a collision, judged by Jellyfin's own case rule.
/// </summary>
public class ChannelIdentityTests
{
    private const string Station = "SUB/WAVE";

    private sealed class RecordingLogger : ILogger<LiveItemFinder>
    {
        public List<LogLevel> Levels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    private static PluginConfiguration Config(string name = Station) => new() { LibraryName = name };

    private static Channel AddChannelEntity(FakeLibrary library, string name = Station)
    {
        var channel = new Channel { Id = library.IdFor("Channel " + name, typeof(Channel)), Name = name };
        library.Items[channel.Id] = channel;
        return channel;
    }

    private static LiveItemFinder Finder(FakeLibrary library, params IChannel[] channels)
        => new(library.Manager, () => channels, NullLogger<LiveItemFinder>.Instance);

    [Fact]
    public void The_channel_is_loaded_by_the_id_jellyfin_derives()
    {
        var library = FakeLibrary.Create();
        var channel = AddChannelEntity(library);

        var resolution = Finder(library).Resolve(Config());

        Assert.Same(channel, resolution.Channel);
        Assert.False(resolution.Collides);
        Assert.Contains(("Channel SUB/WAVE", typeof(Channel)), library.IdRequests);
    }

    [Fact]
    public void A_channel_with_the_same_name_under_another_id_is_not_found()
    {
        // The old name scan would have returned this one: same name, but not the entity Jellyfin serves for the name.
        var library = FakeLibrary.Create();
        library.ListedItems.Add(new Channel { Id = Guid.NewGuid(), Name = Station });

        Assert.Same(ChannelResolution.Missing, Finder(library).Resolve(Config()));
    }

    [Fact]
    public void An_item_under_the_derived_id_that_is_not_a_channel_is_not_returned()
    {
        var library = FakeLibrary.Create();
        library.Items[library.IdFor("Channel " + Station, typeof(Channel))] = new Folder { Name = Station };

        Assert.Null(Finder(library).FindChannel(Config()));
    }

    [Fact]
    public void The_trimmed_name_is_what_the_id_is_derived_from()
    {
        var library = FakeLibrary.Create();
        var channel = AddChannelEntity(library);

        Assert.Same(channel, Finder(library).FindChannel(Config("  " + Station + "  ")));
    }

    [Fact]
    public void Without_a_configuration_or_with_a_blank_tile_name_the_live_radio_fallback_is_used()
    {
        // Live Radio (card L1): the tile name falls back to "Live Radio", no longer to "SUB/WAVE".
        var library = FakeLibrary.Create();
        AddChannelEntity(library);
        var liveRadio = AddChannelEntity(library, PluginConfiguration.DefaultLibraryName);

        Assert.Same(liveRadio, Finder(library).FindChannel(null));
        Assert.Same(liveRadio, Finder(library).FindChannel(Config("   ")));
    }

    [Fact]
    public void Our_own_channel_is_not_a_collision()
    {
        var library = FakeLibrary.Create();
        var channel = AddChannelEntity(library);
        var ours = new JellywaveChannel(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), null!, new RenameBridge(_ => Guid.Empty), () => Config());   // Name: "SUB/WAVE"

        Assert.Same(channel, Finder(library, ours, OtherPluginChannel.Named("Some Other Radio")).FindChannel(Config()));
    }

    [Fact]
    public void Another_plugin_channel_with_the_same_name_collides_and_the_entity_is_withheld()
    {
        var library = FakeLibrary.Create();
        AddChannelEntity(library);

        var resolution = Finder(library, OtherPluginChannel.Named(Station)).Resolve(Config());

        Assert.True(resolution.Collides);
        Assert.Equal(Station, resolution.CollidesWith);
        Assert.Null(resolution.Channel);
    }

    [Fact]
    public void Case_variants_are_distinct_ids_on_a_case_sensitive_server()
    {
        // Jellyfin 12.3's default (EnableCaseSensitiveItemIds = true): GetNewItemId keeps the case.
        var library = FakeLibrary.Create();
        var channel = AddChannelEntity(library);

        var resolution = Finder(library, OtherPluginChannel.Named("sub/wave")).Resolve(Config());

        Assert.False(resolution.Collides);
        Assert.Same(channel, resolution.Channel);
    }

    [Fact]
    public void Case_variants_collide_on_a_case_insensitive_server()
    {
        // EnableCaseSensitiveItemIds = false: GetNewItemId lowercases the key, so "sub/wave" and "SUB/WAVE" are one id.
        var library = FakeLibrary.Create();
        library.CaseSensitiveIds = false;
        AddChannelEntity(library);

        var resolution = Finder(library, OtherPluginChannel.Named("sub/wave")).Resolve(Config());

        Assert.True(resolution.Collides);
        Assert.Null(resolution.Channel);
    }

    [Fact]
    public void One_warning_per_collision_streak()
    {
        var library = FakeLibrary.Create();
        AddChannelEntity(library);
        var logger = new RecordingLogger();
        var channels = new List<IChannel> { OtherPluginChannel.Named(Station) };
        var finder = new LiveItemFinder(library.Manager, () => channels, logger);

        finder.Resolve(Config());
        finder.Resolve(Config());
        finder.Resolve(Config());
        Assert.Equal(new[] { LogLevel.Warning }, logger.Levels);

        channels.Clear();
        finder.Resolve(Config());
        finder.Resolve(Config());
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Information }, logger.Levels);

        channels.Add(OtherPluginChannel.Named(Station));
        finder.Resolve(Config());
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Information, LogLevel.Warning }, logger.Levels);
    }

    [Fact]
    public async Task The_locator_creates_a_missing_entity_and_then_finds_it_by_the_derived_id()
    {
        var library = FakeLibrary.Create();
        var manager = CountingChannelManager.Create();
        Channel? created = null;
        manager.OnCreate = () => created = AddChannelEntity(library);
        var locator = new LibraryChannelLocator(Finder(library), manager.Manager);

        var resolution = await locator.LocateAsync(new PluginConfiguration { LibraryName = Station, Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example" } } }, CancellationToken.None);

        Assert.Equal(1, manager.Creations);
        Assert.NotNull(created);
        Assert.Same(created, resolution.Channel);
    }

    [Fact]
    public async Task The_locator_creates_nothing_on_a_collision_or_without_a_station()
    {
        var library = FakeLibrary.Create();
        var manager = CountingChannelManager.Create();
        var colliding = new LibraryChannelLocator(Finder(library, OtherPluginChannel.Named(Station)), manager.Manager);
        var unconfigured = new LibraryChannelLocator(Finder(library), manager.Manager);

        Assert.True((await colliding.LocateAsync(new PluginConfiguration { LibraryName = Station, Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example" } } }, CancellationToken.None)).Collides);
        Assert.Same(ChannelResolution.Missing, await unconfigured.LocateAsync(new PluginConfiguration { LibraryName = Station }, CancellationToken.None));
        Assert.Equal(0, manager.Creations);
    }

    // Card L7, finding F1: the locator creates the entity whenever a station is listed, not only a SUB/WAVE one.
    private static StationDefinition Plain(string id, string url, bool enabled = true)
        => new() { Id = id, Kind = StationKind.Stream, StreamUrl = url, Enabled = enabled };

    [Fact]
    public async Task The_locator_creates_the_entity_once_for_plain_streams_only()
    {
        var library = FakeLibrary.Create();
        var manager = CountingChannelManager.Create();
        Channel? created = null;
        manager.OnCreate = () => created = AddChannelEntity(library);
        var locator = new LibraryChannelLocator(Finder(library), manager.Manager);
        var config = new PluginConfiguration { LibraryName = Station, Stations = { Plain("a", "https://cdn.example/a.mp3") } };

        var first = await locator.LocateAsync(config, CancellationToken.None);
        var second = await locator.LocateAsync(config, CancellationToken.None);

        Assert.Equal(1, manager.Creations);
        Assert.NotNull(created);
        Assert.Same(created, first.Channel);
        Assert.Same(created, second.Channel);
    }

    [Theory]
    [InlineData("pls")]
    [InlineData("disabled")]
    public async Task The_locator_creates_nothing_when_no_station_is_listed(string why)
    {
        var library = FakeLibrary.Create();
        var manager = CountingChannelManager.Create();
        manager.OnCreate = () => AddChannelEntity(library);
        var locator = new LibraryChannelLocator(Finder(library), manager.Manager);
        var station = why == "pls" ? Plain("a", "https://cdn.example/listen.pls") : Plain("a", "https://cdn.example/a.mp3", enabled: false);

        var resolution = await locator.LocateAsync(new PluginConfiguration { LibraryName = Station, Stations = { station } }, CancellationToken.None);

        Assert.Same(ChannelResolution.Missing, resolution);
        Assert.Equal(0, manager.Creations);
    }

    [Fact]
    public async Task The_locator_creates_the_entity_for_a_listed_plain_stream_next_to_a_refused_sub_wave_station()
    {
        var library = FakeLibrary.Create();
        var manager = CountingChannelManager.Create();
        Channel? created = null;
        manager.OnCreate = () => created = AddChannelEntity(library);
        var locator = new LibraryChannelLocator(Finder(library), manager.Manager);
        var config = new PluginConfiguration
        {
            LibraryName = Station,
            Stations =
            {
                new StationDefinition { Id = "live", BaseUrl = "http://user:pw@radio.example:7700" },
                Plain("a", "https://cdn.example/a.mp3"),
            }
        };

        var resolution = await locator.LocateAsync(config, CancellationToken.None);

        Assert.Equal(1, manager.Creations);
        Assert.Same(created, resolution.Channel);
    }

    [Fact]
    public void The_missing_channel_error_asks_for_a_station_not_a_url()
        => Assert.Equal("the tile does not exist yet (add a station and save)", AccessGranter.MissingChannelError);

    [Fact]
    public void The_registered_channels_are_resolved_on_first_use_not_at_construction()
    {
        var library = FakeLibrary.Create();
        AddChannelEntity(library);
        var built = 0;
        var services = new ServiceCollection()
            .AddSingleton<IChannel>(_ =>
            {
                built++;
                return OtherPluginChannel.Named("Some Other Radio");
            })
            .BuildServiceProvider();

        var finder = new LiveItemFinder(library.Manager, services, NullLogger<LiveItemFinder>.Instance);
        Assert.Equal(0, built);

        Assert.NotNull(finder.FindChannel(Config()));
        Assert.NotNull(finder.FindChannel(Config()));
        Assert.Equal(1, built);
    }

    [Fact]
    public void The_real_registration_resolves_the_finder_and_the_access_check_without_a_cycle()
    {
        var library = FakeLibrary.Create();
        var channel = AddChannelEntity(library);
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        services.AddSingleton(library.Manager);
        services.AddSingleton<IUserManager>(_ => null!);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        var finder = provider.GetRequiredService<LiveItemFinder>();
        Assert.NotNull(provider.GetRequiredService<StationAccess>());
        Assert.Same(channel, finder.FindChannel(Config()));
        Assert.Contains(provider.GetServices<IChannel>(), c => c is JellywaveChannel);
    }
}
