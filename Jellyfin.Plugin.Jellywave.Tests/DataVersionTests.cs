using System;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class DataVersionTests
{
    private static PluginConfiguration Base() => new()
    {
        StationBaseUrl = "http://radio.example:7700",
        StreamPath = "/stream.mp3",
        StationName = "Radio",
        LastChangedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Same_config_gives_same_version()
    {
        Assert.Equal(JellywaveChannel.ComputeDataVersion(Base()), JellywaveChannel.ComputeDataVersion(Base()));
    }

    public static TheoryData<string, Action<PluginConfiguration>> Mutations => new()
    {
        { "base url", c => c.StationBaseUrl = "http://other.example:7700" },
        { "stream path", c => c.StreamPath = "/stream.aac" },
        { "client url", c => c.ClientStreamUrl = "https://public.example/stream.mp3" },
        { "delivery", c => c.Delivery = DeliveryMode.Direct },
        { "name", c => c.StationName = "Other" },
        { "tagline", c => c.StationTagline = "tag" },
        { "bitrate", c => c.StreamBitrateKbps = 128 },
        { "sample rate", c => c.StreamSampleRate = 48000 },
        { "channels", c => c.StreamChannels = 1 },
        { "last changed", c => c.LastChangedUtc = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void Every_item_affecting_field_changes_the_version(string field, Action<PluginConfiguration> mutate)
    {
        var changed = Base();
        mutate(changed);
        Assert.True(
            JellywaveChannel.ComputeDataVersion(Base()) != JellywaveChannel.ComputeDataVersion(changed),
            $"changing {field} did not change the data version");
    }
}
