using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L2: <see cref="JellywaveChannel.DataVersion"/> hashes the tile name and every field of every enabled station, so
/// Jellyfin's three-hour channel cache is dropped whenever anything an item is built from changes, and kept otherwise.
/// </summary>
public class DataVersionTests
{
    private static PluginConfiguration Base() => new()
    {
        LibraryName = "Live Radio",
        Stations =
        {
            new StationDefinition { Id = "live", BaseUrl = "http://radio.example:7700", StreamPath = "/stream.mp3", Name = "Radio" },
            new StationDefinition { Id = "plain", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3", Name = "Plain", SortOrder = 1, LogoUrl = "https://cdn.example/logo.png" },
            new StationDefinition { Id = "off", Kind = StationKind.Stream, StreamUrl = "https://off.example/a.mp3", Name = "Off", SortOrder = 2, Enabled = false },
        },
        LastChangedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static string Version(PluginConfiguration config) => JellywaveChannel.ComputeDataVersion(config);

    [Fact]
    public void Same_config_gives_same_version()
    {
        Assert.Equal(Version(Base()), Version(Base()));
    }

    /// <summary>Every public settable property of a station, so a field added later is covered without editing this test.</summary>
    public static TheoryData<string, int> StationFields()
    {
        var data = new TheoryData<string, int>();
        foreach (var property in typeof(StationDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            data.Add(property.Name, 0);
            data.Add(property.Name, 1);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StationFields))]
    public void Every_field_of_every_enabled_station_changes_the_version(string field, int index)
    {
        var changed = Base();
        var property = typeof(StationDefinition).GetProperty(field)!;
        var station = changed.Stations[index];
        property.SetValue(station, Different(property.PropertyType, property.GetValue(station)));

        Assert.True(Version(Base()) != Version(changed), $"changing {field} of station {index} did not change the data version");
    }

    [Fact]
    public void A_disabled_station_changes_nothing_until_it_is_enabled()
    {
        var changed = Base();
        changed.Stations[2].StreamUrl = "https://elsewhere.example/b.mp3";
        changed.Stations[2].Name = "Renamed";
        Assert.Equal(Version(Base()), Version(changed));

        changed.Stations[2].Enabled = true;
        Assert.NotEqual(Version(Base()), Version(changed));
    }

    [Fact]
    public void Adding_removing_or_reordering_stations_changes_the_version()
    {
        var added = Base();
        added.Stations.Add(new StationDefinition { Id = "new", Kind = StationKind.Stream, StreamUrl = "https://new.example/a.mp3", SortOrder = 5 });
        var removed = Base();
        removed.Stations.RemoveAt(1);
        var swapped = Base();
        swapped.Stations[0].SortOrder = 1;
        swapped.Stations[1].SortOrder = 0;

        Assert.Equal(4, new[] { Version(Base()), Version(added), Version(removed), Version(swapped) }.Distinct().Count());
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted()
    {
        // With a plain separator "a|b" + "c" and "a" + "b|c" would hash alike (StreamUrl and LogoUrl are adjacent).
        var left = Base();
        left.Stations[1].StreamUrl = "https://cdn.example/a.mp3|x";
        left.Stations[1].LogoUrl = "y";
        var right = Base();
        right.Stations[1].StreamUrl = "https://cdn.example/a.mp3";
        right.Stations[1].LogoUrl = "x|y";

        Assert.NotEqual(Version(left), Version(right));
    }

    [Fact]
    public void The_tile_name_and_the_last_change_change_the_version()
    {
        var renamed = Base();
        renamed.LibraryName = "Other Radio";
        var saved = Base();
        saved.LastChangedUtc = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.NotEqual(Version(Base()), Version(renamed));
        Assert.NotEqual(Version(Base()), Version(saved));
    }

    [Fact]
    public void The_legacy_fields_no_longer_change_the_version()
    {
        // After the migration only ConfigMigration reads the 0.4.x fields; the items are built from the stations.
        var changed = Base();
        changed.StationBaseUrl = "http://legacy.example:7700";
        changed.StationName = "Legacy";
        changed.StreamBitrateKbps = 64;
        Assert.Equal(Version(Base()), Version(changed));
    }

    private static object Different(Type type, object? current)
    {
        if (type == typeof(string))
        {
            return (string?)current + "-changed";
        }

        if (type == typeof(int))
        {
            return (int)current! + 7;
        }

        if (type == typeof(bool))
        {
            return !(bool)current!;
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            return values.First(v => !v.Equals(current));
        }

        throw new NotSupportedException("add a way to change a " + type.Name + " field");
    }
}
