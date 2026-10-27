using System;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14: the image type comes from the magic bytes, the size from the header, and malformed input is
/// "unknown" without an exception.
/// </summary>
public class ImageSnifferTests
{
    private const int Max = ImageSniffer.MaxImageDimension;

    public static TheoryData<string, byte[], long, long> ValidHeaders => new()
    {
        { ImageSniffer.Png, ImageFixtures.Png(640, 480), 640, 480 },
        { ImageSniffer.Gif, ImageFixtures.Gif(320, 200, "87a"), 320, 200 },
        { ImageSniffer.Gif, ImageFixtures.Gif(321, 201), 321, 201 },
        { ImageSniffer.Jpeg, ImageFixtures.Jpeg(512, 384), 512, 384 },
        { ImageSniffer.Jpeg, ImageFixtures.Jpeg(300, 200, sof: 0xC2), 300, 200 },   // progressive
        { ImageSniffer.Webp, ImageFixtures.WebpLossy(800, 600), 800, 600 },
        { ImageSniffer.Webp, ImageFixtures.WebpLossless(1000, 750), 1000, 750 },
        { ImageSniffer.Webp, ImageFixtures.WebpExtended(4096, 2048), 4096, 2048 },
    };

    [Theory]
    [MemberData(nameof(ValidHeaders))]
    public void Each_format_is_identified_with_its_dimensions(string mime, byte[] bytes, long width, long height)
    {
        Assert.Equal(mime, ImageSniffer.Identify(bytes, out var w, out var h));
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.True(ImageSniffer.TryAccept(bytes, out var accepted));
        Assert.Equal(mime, accepted);
    }

    [Fact]
    public void The_largest_allowed_image_is_accepted()
        => Assert.True(ImageSniffer.TryAccept(ImageFixtures.Png(Max, Max), out _));

    public static TheoryData<byte[]> Oversize => new()
    {
        ImageFixtures.Png(Max + 1, 1),
        ImageFixtures.Png(1, Max + 1),
        ImageFixtures.Png(uint.MaxValue, uint.MaxValue),
        ImageFixtures.Gif(Max + 1, 16),
        ImageFixtures.Jpeg(16, Max + 1),
        ImageFixtures.WebpLossless(Max + 1, 16),
        ImageFixtures.WebpExtended(16_000, 16_000),
        ImageFixtures.WebpLossy(Max + 1, 16),
    };

    [Theory]
    [MemberData(nameof(Oversize))]
    public void Oversize_dimensions_are_rejected(byte[] bytes)
    {
        Assert.NotNull(ImageSniffer.Identify(bytes, out _, out _));
        Assert.False(ImageSniffer.TryAccept(bytes, out var mime));
        Assert.Null(mime);
    }

    public static TheoryData<byte[]> ZeroSized => new()
    {
        ImageFixtures.Png(0, 16),
        ImageFixtures.Gif(16, 0),
        ImageFixtures.Jpeg(16, 0),   // height 0 = defined later by DNL; not accepted
        ImageFixtures.WebpLossy(0, 16),
    };

    [Theory]
    [MemberData(nameof(ZeroSized))]
    public void A_zero_dimension_is_rejected(byte[] bytes)
        => Assert.False(ImageSniffer.TryAccept(bytes, out _));

    [Fact]
    public void A_truncated_header_is_unknown_at_every_length()
    {
        var fixtures = new[]
        {
            ImageFixtures.Png(64, 64), ImageFixtures.Gif(64, 64), ImageFixtures.Jpeg(64, 64),
            ImageFixtures.WebpLossy(64, 64), ImageFixtures.WebpLossless(64, 64), ImageFixtures.WebpExtended(64, 64)
        };
        foreach (var full in fixtures)
        {
            Assert.True(ImageSniffer.TryAccept(full, out _));
            var headerLength = HeaderLength(full);
            for (var length = 0; length < headerLength; length++)
            {
                Assert.False(ImageSniffer.TryAccept(full.AsSpan(0, length), out _), "accepted " + length + " of " + full.Length + " bytes");
            }
        }
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E })]                     // "<svg>"
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00, 0x0C, 0x00 })]               // BMP
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0 })]   // JPEG scan before any frame
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01 })]               // segment length below 2
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0x00 })]         // segment longer than the data
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x04, 0x08, 0x00 })]   // SOF too short for its sizes
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xFF })]               // fill bytes to the end
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x12 })]         // no marker after a segment
    public void Garbage_is_unknown(byte[] bytes)
    {
        Assert.Null(ImageSniffer.Identify(bytes, out var w, out var h));
        Assert.Equal(0, w);
        Assert.Equal(0, h);
        Assert.False(ImageSniffer.TryAccept(bytes, out _));
    }

    [Fact]
    public void A_webp_lossy_interframe_or_bad_start_code_is_unknown()
    {
        var interframe = ImageFixtures.WebpLossy(64, 64);
        interframe[20] |= 1;
        Assert.Null(ImageSniffer.Identify(interframe, out _, out _));

        var badStart = ImageFixtures.WebpLossy(64, 64);
        badStart[24] = 0x00;
        Assert.Null(ImageSniffer.Identify(badStart, out _, out _));
    }

    [Fact]
    public void A_png_without_ihdr_first_is_unknown()
    {
        var bytes = ImageFixtures.Png(64, 64);
        bytes[12] = (byte)'X';
        Assert.Null(ImageSniffer.Identify(bytes, out _, out _));
    }

    [Fact]
    public void Random_bytes_after_each_signature_never_throw()
    {
        var random = new Random(14);
        byte[][] prefixes =
        [
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0xFF, 0xD8, 0xFF], "GIF89a"u8.ToArray(),
            [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray()]
        ];
        foreach (var prefix in prefixes)
        {
            for (var round = 0; round < 2000; round++)
            {
                var tail = new byte[random.Next(0, 64)];
                random.NextBytes(tail);
                var bytes = prefix.Concat(tail).ToArray();
                _ = ImageSniffer.TryAccept(bytes, out _);
            }
        }
    }

    // Bytes the sniffer needs: the fixed header for PNG, GIF and WebP; up to the frame header's width for JPEG.
    private static int HeaderLength(byte[] bytes) => bytes[0] switch
    {
        0x89 => 24,
        (byte)'G' => 10,
        (byte)'R' => bytes[15] == (byte)'L' ? 25 : 30,
        0xFF => bytes.Length - 2,   // without EOI the frame is still complete; one byte less cuts into the SOF segment
        _ => throw new ArgumentOutOfRangeException(nameof(bytes))
    };
}
