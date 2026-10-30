using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L6: parsing a station's in-stream metadata block and its <c>icy-metaint</c> header. Station-supplied, so every odd
/// shape a real server sends (quotes in titles, no terminator, Latin-1, NUL padding) and every bound is pinned here.
/// </summary>
public class IcyMetadataTests
{
    /// <summary>A metadata block as a server sends it: the text, then NUL padding up to a multiple of 16 bytes.</summary>
    internal static byte[] Block(byte[] text)
    {
        var length = (text.Length + IcyMetadata.BlockUnit - 1) / IcyMetadata.BlockUnit * IcyMetadata.BlockUnit;
        var block = new byte[length];
        text.CopyTo(block, 0);
        return block;
    }

    internal static byte[] Block(string text) => Block(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("StreamTitle='Artist - Title';StreamUrl='';", "Artist - Title")]
    [InlineData("StreamTitle='Artist - Title';", "Artist - Title")]
    [InlineData("StreamTitle='Guns N' Roses - Don't Cry';StreamUrl='';", "Guns N' Roses - Don't Cry")]
    [InlineData("StreamTitle='It';s here - 'Quoted';';StreamUrl='http://x.example/';", "It';s here - 'Quoted';")]
    [InlineData("StreamTitle='Ends in quote'';", "Ends in quote'")]
    [InlineData("StreamTitle='Only title';  ", "Only title")]
    [InlineData("StreamTitle='Two fields'; StreamUrl='';", "Two fields")]
    [InlineData("streamtitle='lower case key';", "lower case key")]
    [InlineData("StreamUrl='';StreamTitle='Title second';", "Title second")]
    [InlineData("StreamTitle='No terminator", "No terminator")]
    [InlineData("StreamTitle='Half terminator'", "Half terminator")]
    [InlineData("StreamTitle='Half terminator';", "Half terminator")]
    [InlineData("StreamTitle='a;b';", "a;b")]
    [InlineData("StreamTitle='<b>bold</b> & co';", "<b>bold</b> & co")]
    [InlineData("StreamTitle='';StreamUrl='';", "")]
    [InlineData("StreamTitle='", "")]
    public void Reads_the_stream_title(string text, string expected)
    {
        Assert.Equal(expected, IcyMetadata.StreamTitle(Block(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("StreamUrl='http://x.example/';")]
    [InlineData("Title='not the field';")]
    [InlineData("StreamTitle=no quotes;")]
    public void A_block_without_a_stream_title_has_none(string text)
    {
        Assert.Null(IcyMetadata.StreamTitle(Block(text)));
    }

    [Fact]
    public void An_empty_block_has_no_title()
    {
        Assert.Null(IcyMetadata.StreamTitle([]));
        Assert.Null(IcyMetadata.StreamTitle(new byte[IcyMetadata.BlockUnit]));
    }

    [Fact]
    public void Utf8_titles_decode_as_utf8()
    {
        Assert.Equal("Björk - Jóga", IcyMetadata.StreamTitle(Block("StreamTitle='Björk - Jóga';")));
        Assert.Equal("坂本龍一 - Merry Christmas", IcyMetadata.StreamTitle(Block("StreamTitle='坂本龍一 - Merry Christmas';")));
    }

    [Fact]
    public void Non_utf8_bytes_fall_back_to_latin1()
    {
        // "Björk - Jóga" as an older server sends it: Latin-1 bytes, invalid as UTF-8.
        var latin1 = Encoding.Latin1.GetBytes("StreamTitle='Björk - Jóga';");
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(latin1));
        Assert.Equal("Björk - Jóga", IcyMetadata.StreamTitle(Block(latin1)));
    }

    [Fact]
    public void A_byte_order_mark_is_dropped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("StreamTitle='BOM';")).ToArray();
        Assert.Equal("BOM", IcyMetadata.StreamTitle(Block(bytes)));
    }

    [Fact]
    public void Control_characters_and_nul_bytes_inside_survive_the_parse_for_the_cleaner()
    {
        // The parser keeps what was sent; StationText.Clean (the sampler) turns control characters into spaces.
        var title = IcyMetadata.StreamTitle(Block("StreamTitle='Line\r\none\0two';"));
        Assert.Equal("Line\r\none\0two", title);
        Assert.Equal("Line one two", StationText.Clean(title, IcyMetadata.MaxTitleLength));
    }

    [Fact]
    public void The_largest_block_parses_and_cleans_to_the_title_limit()
    {
        var text = "StreamTitle='" + new string('x', IcyMetadata.MaxBlockBytes - 15) + "';";
        Assert.Equal(IcyMetadata.MaxBlockBytes, Encoding.UTF8.GetByteCount(text));
        var title = IcyMetadata.StreamTitle(Block(text));
        Assert.Equal(IcyMetadata.MaxBlockBytes - 15, title!.Length);
        var cleaned = StationText.Clean(title, IcyMetadata.MaxTitleLength)!;
        Assert.Equal(IcyMetadata.MaxTitleLength, cleaned.Length);
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // An emoji is two UTF-16 units; one that straddles the cut goes whole, before the ellipsis.
        var title = new string('a', IcyMetadata.MaxTitleLength - 2) + "\U0001F3B5" + "tail";
        var cleaned = StationText.Clean(title, IcyMetadata.MaxTitleLength)!;
        Assert.Equal(new string('a', IcyMetadata.MaxTitleLength - 2) + "…", cleaned);
        Assert.DoesNotContain(cleaned, char.IsSurrogate);

        var fits = new string('a', IcyMetadata.MaxTitleLength - 3) + "\U0001F3B5" + "tail";
        Assert.Equal(new string('a', IcyMetadata.MaxTitleLength - 3) + "\U0001F3B5…", StationText.Clean(fits, IcyMetadata.MaxTitleLength));
    }

    [Theory]
    [InlineData("16000", 16000)]
    [InlineData(" 8192 ", 8192)]
    [InlineData("1", 1)]
    [InlineData("65536", 65536)]
    public void Accepts_an_icy_metaint_within_the_bound(string value, int expected)
    {
        Assert.True(IcyMetadata.TryParseMetaInt(value, out var metaInt));
        Assert.Equal(expected, metaInt);
        Assert.Equal(65536, IcyMetadata.MaxMetaInt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+16000")]
    [InlineData("65537")]
    [InlineData("1000000")]
    [InlineData("99999999999")]
    [InlineData("16000, 16000")]
    [InlineData("16k")]
    [InlineData("0x4000")]
    [InlineData("16000.0")]
    public void Refuses_any_other_icy_metaint(string? value)
    {
        Assert.False(IcyMetadata.TryParseMetaInt(value, out var metaInt));
        Assert.Equal(0, metaInt);
    }

    [Fact]
    public void The_read_cap_is_the_audio_the_length_byte_and_the_largest_block()
    {
        Assert.Equal(16000 + 1 + (255 * 16), IcyMetadata.ReadCap(16000));
        Assert.Equal(IcyMetadata.MaxMetaInt + 1 + 4080, IcyMetadata.ReadCap(IcyMetadata.MaxMetaInt));
    }
}
