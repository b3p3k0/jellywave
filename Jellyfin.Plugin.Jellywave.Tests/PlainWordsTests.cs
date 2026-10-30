using System;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L11 (C): the server's texts the configuration page shows, in the page's words: "tile", not "channel"; an ordinary
/// stream is a "Radio stream"; nothing says "added" before Add to list and Save. The tests elsewhere compare with these
/// constants, so the words themselves are pinned here.
/// </summary>
public class PlainWordsTests
{
    [Fact]
    public void A_plain_stream_reads_as_a_radio_stream()
        => Assert.Equal("Radio stream.", StationProbe.PlainStream);

    [Fact]
    public void The_probe_lines_for_a_plain_stream_say_nothing_was_added()
    {
        foreach (var line in new[] { StationProbe.PlainStream, StationProbe.DetectionTimedOut, StationProbe.SubWaveOtherServer })
        {
            Assert.StartsWith("Radio stream", line, StringComparison.Ordinal);
            Assert.DoesNotContain("added", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plain stream", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Radio stream. The SUB/WAVE check did not finish in time; press Check again if this is a SUB/WAVE station.", StationProbe.DetectionTimedOut);
        Assert.Equal("Radio stream: a SUB/WAVE station answers here, but its stream is not under its address.", StationProbe.SubWaveOtherServer);
    }

    [Fact]
    public void A_refused_kind_is_named_as_the_page_names_the_types()
        => Assert.Equal("its type is not SUB/WAVE or Stream", StationSources.RefusedKind);

    [Fact]
    public void The_page_texts_say_tile_not_channel()
    {
        Assert.Equal("another plugin's tile has the same name", ChannelResolution.CollisionStatus);
        Assert.Equal("the tile does not exist yet (add a station and save)", AccessGranter.MissingChannelError);
        foreach (var text in new[] { ChannelResolution.CollisionStatus, AccessGranter.MissingChannelError })
        {
            Assert.DoesNotContain("channel", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
