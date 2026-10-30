using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Jellywave.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15b removed <c>GrantAccessToAllUsers</c>. A configuration saved by 0.2.0.0–0.3.4.0 still carries the element,
/// and Jellyfin's <c>BasePlugin</c> replaces a configuration it cannot read with defaults, so the file must still load.
/// The serializer is built the way Jellyfin's <c>MyXmlSerializer</c> builds it: <c>new XmlSerializer(type)</c>
/// reading through <c>XmlReader.Create</c>.
/// </summary>
public class LegacyConfigurationTests
{
    private const string SavedBy034 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
          <StreamPath>/stream.mp3</StreamPath>
          <ClientStreamUrl />
          <StationName>Test Radio</StationName>
          <StationTagline>tagline</StationTagline>
          <Delivery>Direct</Delivery>
          <NowPlayingEnabled>false</NowPlayingEnabled>
          <CoverArtEnabled>true</CoverArtEnabled>
          <NowPlayingPollSeconds>30</NowPlayingPollSeconds>
          <StreamBitrateKbps>128</StreamBitrateKbps>
          <StreamSampleRate>48000</StreamSampleRate>
          <StreamChannels>1</StreamChannels>
          <GrantAccessToAllUsers>true</GrantAccessToAllUsers>
          <LastChangedUtc>2026-10-27T18:00:00Z</LastChangedUtc>
        </PluginConfiguration>
        """;

    [Fact]
    public void A_config_saved_with_the_removed_grant_setting_still_loads()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var unknown = new List<string>();
        serializer.UnknownElement += (_, e) => unknown.Add(e.Element.Name);
        PluginConfiguration config;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(SavedBy034)))
        using (var reader = XmlReader.Create(stream))
        {
            config = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        // Seen and ignored, not an error: the grant setting (card 15b) and the Direct fields (card L10).
        Assert.Equal(new[] { "ClientStreamUrl", "Delivery", "GrantAccessToAllUsers" }, unknown);
        Assert.Equal("http://radio.example:7700", config.StationBaseUrl);
        Assert.Equal("Test Radio", config.StationName);
        Assert.False(config.NowPlayingEnabled);
        Assert.Equal(30, config.NowPlayingPollSeconds);
        Assert.Equal(128, config.StreamBitrateKbps);
        Assert.Equal(1, config.StreamChannels);
    }

    [Fact]
    public void The_next_save_drops_the_removed_element()
    {
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, new PluginConfiguration());

        Assert.DoesNotContain("GrantAccessToAllUsers", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Delivery", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ClientStreamUrl", writer.ToString(), StringComparison.Ordinal);
    }
}
