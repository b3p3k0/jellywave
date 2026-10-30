using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a (HI decision D9): covers, logos and avatars must be one still image. The sniffer sized only the first frame
/// from the header, so a small file could carry many frames for Jellyfin's image processor. A bounded walk of the
/// structure (GIF blocks, PNG chunks, WebP RIFF chunks) refuses animation and any malformed or contradictory structure;
/// the byte and dimension limits stay.
/// </summary>
public class ImageAnimationTests
{
    private const ushort Side = 64;

    public static TheoryData<string, byte[]> Stills => new()
    {
        { "png", ImageFixtures.Png(Side, Side) },
        { "png with two IDAT and a text chunk", ImageFixtures.Png(Side, Side, "IDAT", "tEXt", "IDAT") },
        { "gif", ImageFixtures.Gif(Side, Side) },
        { "gif with a global table", WithGlobalTable(ImageFixtures.Gif(Side, Side)) },
        { "gif with a loop extension", WithLoopExtension(ImageFixtures.Gif(Side, Side)) },
        { "gif frame inside the screen", GifWithFrames(Side, Side, ImageFixtures.GifFrame(8, 8, 16, 16)) },
        { "webp lossy", ImageFixtures.WebpLossy(Side, Side) },
        { "webp lossless", ImageFixtures.WebpLossless(Side, Side) },
        { "webp extended", ImageFixtures.WebpExtended(Side, Side) },
        { "webp extended with alpha and exif", ImageFixtures.WebpExtended(Side, Side, 0x18, ImageFixtures.WebpChunk("ALPH", [0, 1, 2]), Lossy(Side, Side), ImageFixtures.WebpChunk("EXIF", [1, 2])) },
        { "jpeg", ImageFixtures.Jpeg(Side, Side) },
    };

    public static TheoryData<string, byte[]> Animated => new()
    {
        { "gif with two frames", ImageFixtures.Gif(Side, Side, "89a", frames: 2) },
        { "gif with many frames", ImageFixtures.Gif(Side, Side, "89a", frames: 500) },
        { "apng: acTL before IDAT", ImageFixtures.Png(Side, Side, "acTL", "fcTL", "IDAT", "fcTL", "fdAT") },
        { "acTL after IDAT", ImageFixtures.Png(Side, Side, "IDAT", "acTL") },
        { "a frame chunk without acTL", ImageFixtures.Png(Side, Side, "fcTL", "IDAT") },
        { "webp animation flag", ImageFixtures.WebpExtended(Side, Side, 0x02, ImageFixtures.WebpChunk("ANIM", new byte[6]), ImageFixtures.WebpChunk("ANMF", new byte[16])) },
        { "webp animation flag with one image", ImageFixtures.WebpExtended(Side, Side, 0x02, ImageFixtures.WebpChunk("VP8L", ImageFixtures.Vp8l(Side, Side))) },
        { "webp ANIM chunk without the flag", ImageFixtures.WebpExtended(Side, Side, 0, ImageFixtures.WebpChunk("ANIM", new byte[6]), ImageFixtures.WebpChunk("VP8L", ImageFixtures.Vp8l(Side, Side))) },
        { "webp ANMF chunk without the flag", ImageFixtures.WebpExtended(Side, Side, 0, ImageFixtures.WebpChunk("ANMF", new byte[16])) },
    };

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "gif without a trailer", ImageFixtures.Gif(Side, Side)[..^1] },
        { "gif with no frame", GifWithFrames(Side, Side) },
        { "gif frame wider than the screen", GifWithFrames(Side, Side, ImageFixtures.GifFrame(0, 0, Side + 1, Side)) },
        { "gif frame past the screen's edge", GifWithFrames(Side, Side, ImageFixtures.GifFrame(Side - 4, 0, 8, 8)) },
        { "gif frame of zero size", GifWithFrames(Side, Side, ImageFixtures.GifFrame(0, 0, 0, 0)) },
        { "gif code size out of range", Patched(GifWithFrames(Side, Side, ImageFixtures.GifFrame(0, 0, Side, Side)), 13 + 10, 12) },
        { "gif sub-block past the end", GifWithFrames(Side, Side, [0x2C, 0, 0, 0, 0, 64, 0, 64, 0, 0, 0x02, 0x40, 0x01]) },
        { "gif unknown block", GifWithFrames(Side, Side, [0x99]) },
        { "gif global table past the end", Patched(ImageFixtures.Gif(Side, Side), 10, 0x87) },
        { "png without IEND", ImageFixtures.Png(Side, Side)[..^12] },
        { "png without IDAT", ImageFixtures.Png(Side, Side, []) },
        { "png with a second IHDR", ImageFixtures.Png(Side, Side, "IDAT", "IHDR") },
        { "png chunk longer than the file", Patched(ImageFixtures.Png(Side, Side), 33, 0x7F) },
        { "png chunk length over 2^31", Patched(ImageFixtures.Png(Side, Side), 33, 0xFF) },
        { "png chunk name not letters", Patched(ImageFixtures.Png(Side, Side), 37, (byte)'1') },
        { "webp riff size past the end", Patched(ImageFixtures.WebpLossy(Side, Side), 4, 0xFF) },
        { "webp chunk past the riff size", Patched(ImageFixtures.WebpLossy(Side, Side), 16, 0x40) },
        { "webp extended without an image", ImageFixtures.WebpExtended(Side, Side, 0) },
        { "webp extended with two images", ImageFixtures.WebpExtended(Side, Side, 0, Lossy(Side, Side), Lossy(Side, Side)) },
        { "webp image smaller than the canvas", ImageFixtures.WebpExtended(Side, Side, 0, Lossy(Side / 2, Side)) },
        { "webp image larger than the canvas", ImageFixtures.WebpExtended(Side, Side, 0, ImageFixtures.WebpChunk("VP8L", ImageFixtures.Vp8l(4096, 4096))) },
        { "webp simple then VP8X", ImageFixtures.RiffOf([.. Lossy(Side, Side), .. ImageFixtures.WebpChunk("VP8X", new byte[10])]) },
        { "webp simple with alpha", ImageFixtures.RiffOf([.. Lossy(Side, Side), .. ImageFixtures.WebpChunk("ALPH", [1])]) },
        { "webp simple with a second image", ImageFixtures.RiffOf([.. Lossy(Side, Side), .. Lossy(Side, Side)]) },
        { "webp odd chunk without its pad byte", ImageFixtures.RiffOf(ImageFixtures.WebpChunk("VP8L", ImageFixtures.Vp8l(Side, Side))[..^1]) },
    };

    public static TheoryData<string, byte[]> Jpegs => new()
    {
        { "baseline, one scan", ImageFixtures.JpegScans(Side, Side, 1, sof: 0xC0) },
        { "progressive, ten scans", ImageFixtures.JpegScans(Side, Side, 10) },
        { "progressive with restart markers", ImageFixtures.JpegScans(Side, Side, 10, restart: true) },
        { "baseline with EXIF and restart markers", ImageFixtures.JpegScans(Side, Side, 1, sof: 0xC0, restart: true, exif: true) },
        { "the most scans allowed", ImageFixtures.JpegScans(Side, Side, ImageStructure.MaxJpegScans) },
        { "cut short in a scan", ImageFixtures.JpegScans(Side, Side, 10)[..^40] },
        { "stray bytes between segments", WithStrayByte(ImageFixtures.JpegScans(Side, Side, 3)) },
    };

    [Theory]
    [MemberData(nameof(Jpegs))]
    public void Real_world_shaped_jpegs_are_accepted(string what, byte[] bytes)
        => Assert.True(ImageSniffer.TryAccept(bytes, out _), what);

    [Fact]
    public void A_jpeg_with_more_scans_than_the_limit_is_refused()
    {
        // Each scan of a progressive JPEG is one more decode pass: the cost D9 is about, in another format.
        var many = ImageFixtures.JpegScans(Side, Side, ImageStructure.MaxJpegScans + 1, restart: true);
        Assert.Equal(ImageSniffer.Jpeg, ImageSniffer.Identify(many, out _, out _));
        Assert.False(ImageSniffer.TryAccept(many, out _));
    }

    [Fact]
    public void A_jpeg_segment_length_below_two_is_refused()
    {
        var bytes = ImageFixtures.JpegScans(Side, Side, 2).ToList();
        var dht = bytes.FindIndex(b => b == 0xC4) - 1;   // FF C4, after the frame header
        bytes[dht + 3] = 0x01;
        Assert.False(ImageSniffer.TryAccept(bytes.ToArray(), out _));
    }

    [Theory]
    [MemberData(nameof(Stills))]
    public void One_still_image_is_accepted(string what, byte[] bytes)
        => Assert.True(ImageSniffer.TryAccept(bytes, out _), what);

    [Theory]
    [MemberData(nameof(Animated))]
    public void An_animation_is_refused_though_its_header_size_is_fine(string what, byte[] bytes)
    {
        // The header alone passes (type and first-frame size); only the walk refuses it.
        Assert.NotNull(ImageSniffer.Identify(bytes, out var width, out var height));
        Assert.Equal((Side, Side), ((ushort)width, (ushort)height));
        Assert.False(ImageSniffer.TryAccept(bytes, out var mime), what);
        Assert.Null(mime);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_or_contradictory_structure_is_refused(string what, byte[] bytes)
        => Assert.False(ImageSniffer.TryAccept(bytes, out _), what);

    [Fact]
    public void Every_cut_of_a_still_image_is_refused_or_still_one_image()
    {
        // A stream cut short never makes a partial image look whole: every prefix of every format is refused, apart from
        // a cut that falls after the end marker (bytes there are not read).
        foreach (var full in new[] { ImageFixtures.Png(Side, Side), ImageFixtures.Gif(Side, Side), ImageFixtures.WebpExtended(Side, Side), ImageFixtures.WebpLossless(Side, Side) })
        {
            for (var length = 0; length < full.Length; length++)
            {
                Assert.False(ImageSniffer.TryAccept(full.AsSpan(0, length), out _), "accepted " + length + " of " + full.Length + " bytes");
            }
        }
    }

    [Fact]
    public void Random_chunk_and_block_bytes_never_throw()
    {
        var random = new Random(2);
        var seeds = new[] { ImageFixtures.Png(Side, Side), ImageFixtures.Gif(Side, Side), ImageFixtures.WebpExtended(Side, Side), ImageFixtures.WebpLossy(Side, Side), ImageFixtures.JpegScans(Side, Side, 10, restart: true) };
        foreach (var seed in seeds)
        {
            for (var round = 0; round < 2000; round++)
            {
                var bytes = seed.ToArray();
                for (var flips = random.Next(1, 4); flips > 0; flips--)
                {
                    bytes[random.Next(8, bytes.Length)] = (byte)random.Next(256);
                }

                _ = ImageSniffer.TryAccept(bytes, out _);
            }
        }
    }

    /// <summary>The same JPEG with a stray byte before its first scan (after the frame header, which the sniffer reads strictly).</summary>
    private static byte[] WithStrayByte(byte[] jpeg)
    {
        var bytes = jpeg.ToList();
        var sos = Enumerable.Range(0, bytes.Count - 1).First(i => bytes[i] == 0xFF && bytes[i + 1] == 0xDA);
        bytes.Insert(sos, 0x42);
        return [.. bytes];
    }

    private static byte[] Lossy(int width, int height)
    {
        var payload = ImageFixtures.WebpLossy((ushort)width, (ushort)height)[20..30];
        return ImageFixtures.WebpChunk("VP8 ", payload);
    }

    private static byte[] Patched(byte[] bytes, int offset, byte value)
    {
        var copy = bytes.ToArray();
        copy[offset] = value;
        return copy;
    }

    /// <summary>A GIF header and logical screen, the given blocks as they are, then the trailer.</summary>
    private static byte[] GifWithFrames(ushort width, ushort height, params byte[][] blocks)
    {
        var bytes = new List<byte>(ImageFixtures.Gif(width, height)[..13]);
        foreach (var block in blocks)
        {
            bytes.AddRange(block);
        }

        bytes.Add(0x3B);
        return [.. bytes];
    }

    /// <summary>The same GIF with a global colour table of two entries after the logical screen.</summary>
    private static byte[] WithGlobalTable(byte[] gif)
    {
        var bytes = gif.ToList();
        bytes[10] = 0x80;   // a table of 2^(0+1) entries
        bytes.InsertRange(13, new byte[6]);
        return [.. bytes];
    }

    /// <summary>The same GIF with a NETSCAPE2.0 loop extension: a loop count says nothing about frames.</summary>
    private static byte[] WithLoopExtension(byte[] gif)
    {
        var bytes = gif.ToList();
        var extension = new List<byte> { 0x21, 0xFF, 0x0B };
        extension.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        extension.AddRange([0x03, 0x01, 0x00, 0x00, 0x00]);
        bytes.InsertRange(13, extension);
        return [.. bytes];
    }
}
