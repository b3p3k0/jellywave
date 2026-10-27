using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Identifies a station image from its bytes (JPEG, PNG, GIF or WebP) and reads its size from the headers, so an
/// image is only handed to Jellyfin's decoder when its type is known and its dimensions are bounded. Every read is
/// bounds-checked; malformed or truncated input is "unknown", never an exception. GIF frame count is not checked
/// (accepted risk, see DESIGN.md).
/// </summary>
public static class ImageSniffer
{
    /// <summary>
    /// Longest side accepted, in pixels. The station serves 512 px covers.
    /// </summary>
    public const int MaxImageDimension = 4096;

    /// <summary>
    /// Largest area accepted, in pixels (4096 × 4096).
    /// </summary>
    public const long MaxImagePixels = 16_777_216;

    /// <summary>The JPEG media type.</summary>
    public const string Jpeg = "image/jpeg";

    /// <summary>The PNG media type.</summary>
    public const string Png = "image/png";

    /// <summary>The GIF media type.</summary>
    public const string Gif = "image/gif";

    /// <summary>The WebP media type.</summary>
    public const string Webp = "image/webp";

    // PNG: signature (8), IHDR length (4), "IHDR" (4), width (4), height (4).
    private const int PngHeaderLength = 24;

    // GIF: signature (6), logical screen width (2), height (2).
    private const int GifHeaderLength = 10;

    // RIFF: "RIFF" (4), size (4), "WEBP" (4), first chunk FourCC (4), chunk size (4); chunk data from offset 20.
    private const int WebpChunkData = 20;

    // VP8 lossy: frame tag (3), start code (3), width (2), height (2).
    private const int Vp8HeaderLength = WebpChunkData + 10;

    // VP8L: signature byte (1), packed sizes (4).
    private const int Vp8lHeaderLength = WebpChunkData + 5;

    // VP8X: flags (4), canvas width - 1 (3), canvas height - 1 (3).
    private const int Vp8xHeaderLength = WebpChunkData + 10;

    private const int Vp8SideMask = 0x3FFF;
    private const byte Vp8lSignature = 0x2F;
    private const byte JpegMarkerPrefix = 0xFF;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Vp8StartCode => [0x9D, 0x01, 0x2A];

    /// <summary>
    /// Decides whether an image may be used, and with which media type.
    /// </summary>
    /// <param name="data">The image bytes.</param>
    /// <param name="mimeType">The detected media type when accepted; the declared one plays no part.</param>
    /// <returns>
    /// True when the type is known, both sides are at least 1 and at most <see cref="MaxImageDimension"/>, and the area
    /// is at most <see cref="MaxImagePixels"/>.
    /// </returns>
    public static bool TryAccept(ReadOnlySpan<byte> data, [NotNullWhen(true)] out string? mimeType)
    {
        mimeType = null;
        var detected = Identify(data, out var width, out var height);
        if (detected is null
            || width <= 0
            || height <= 0
            || width > MaxImageDimension
            || height > MaxImageDimension
            || width * height > MaxImagePixels)
        {
            return false;
        }

        mimeType = detected;
        return true;
    }

    /// <summary>
    /// Detects the type from the magic bytes and reads the dimensions from the header. Internal for tests.
    /// </summary>
    /// <param name="data">The image bytes.</param>
    /// <param name="width">The width in pixels, or 0.</param>
    /// <param name="height">The height in pixels, or 0.</param>
    /// <returns>The media type, or null when the type is unknown or its header is malformed or truncated.</returns>
    internal static string? Identify(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = 0;
        height = 0;

        if (data.StartsWith(PngSignature))
        {
            return TryReadPng(data, out width, out height) ? Png : null;
        }

        if (data.StartsWith(JpegSignature))
        {
            return TryReadJpeg(data, out width, out height) ? Jpeg : null;
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return TryReadGif(data, out width, out height) ? Gif : null;
        }

        if (data.Length >= WebpChunkData && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return TryReadWebp(data, out width, out height) ? Webp : null;
        }

        return null;
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = 0;
        height = 0;
        if (data.Length < PngHeaderLength || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        return true;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = 0;
        height = 0;
        if (data.Length < GifHeaderLength)
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        return true;
    }

    private static bool TryReadWebp(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = 0;
        height = 0;
        var chunk = data.Slice(12, 4);

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Lossy: a keyframe has bit 0 of the frame tag clear and the start code after the 3-byte tag.
            if (data.Length < Vp8HeaderLength || (data[WebpChunkData] & 1) != 0 || !data.Slice(WebpChunkData + 3, 3).SequenceEqual(Vp8StartCode))
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(WebpChunkData + 6, 2)) & Vp8SideMask;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(WebpChunkData + 8, 2)) & Vp8SideMask;
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Lossless: 14 bits of width - 1, then 14 bits of height - 1, little-endian.
            if (data.Length < Vp8lHeaderLength || data[WebpChunkData] != Vp8lSignature)
            {
                return false;
            }

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(WebpChunkData + 1, 4));
            width = (bits & Vp8SideMask) + 1;
            height = ((bits >> 14) & Vp8SideMask) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Extended: 24-bit canvas width - 1 and height - 1 after 4 bytes of flags.
            if (data.Length < Vp8xHeaderLength)
            {
                return false;
            }

            width = ReadUInt24LittleEndian(data.Slice(WebpChunkData + 4, 3)) + 1;
            height = ReadUInt24LittleEndian(data.Slice(WebpChunkData + 7, 3)) + 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the JPEG markers from after SOI to the first start-of-frame (SOF0 to SOF15, except DHT C4, JPG C8 and
    /// DAC CC) and reads its height and width. A scan or end of image before any frame header is malformed.
    /// </summary>
    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = 0;
        height = 0;
        var i = 2;
        while (i < data.Length)
        {
            if (data[i] != JpegMarkerPrefix)
            {
                return false;
            }

            // Any number of 0xFF fill bytes may precede a marker.
            while (i < data.Length && data[i] == JpegMarkerPrefix)
            {
                i++;
            }

            if (i >= data.Length)
            {
                return false;
            }

            var marker = data[i++];
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                // TEM, SOI and RSTn stand alone, with no length.
                continue;
            }

            if (marker is 0x00 or 0xD9 or 0xDA)
            {
                // A stuffed byte, EOI or SOS before any frame header.
                return false;
            }

            if (data.Length - i < 2)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
            if (length < 2 || length > data.Length - i)
            {
                return false;
            }

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // Length (2), precision (1), height (2), width (2).
                if (length < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2));
                return true;
            }

            i += length;
        }

        return false;
    }

    private static long ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
