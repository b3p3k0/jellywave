using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Minimal images built in code, one per format, with the dimensions written where each format keeps them. Card R2a: each
/// is a complete structure (PNG chunks to IEND, GIF blocks to the trailer, every RIFF chunk inside its size), because the
/// sniffer now walks it to count frames; the pixel data is filler, which the walk never decodes.
/// </summary>
internal static class ImageFixtures
{
    public const int CoverSide = 512;

    public static byte[] Png(uint width, uint height) => Png(width, height, "IDAT");

    /// <summary>
    /// A PNG: the signature, IHDR, then each named chunk with two filler bytes (an empty name is left out), then IEND.
    /// CRCs are zero: the walk does not check them, Jellyfin's decoder would.
    /// </summary>
    public static byte[] Png(uint width, uint height, params string[] chunks)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        PngChunk(bytes, "IHDR", ihdr);
        foreach (var chunk in chunks)
        {
            PngChunk(bytes, chunk, [0x78, 0x9C]);
        }

        PngChunk(bytes, "IEND", []);
        return [.. bytes];
    }

    public static void PngChunk(List<byte> bytes, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        bytes.AddRange(length);
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange(new byte[4]);   // CRC
    }

    public static byte[] Gif(ushort width, ushort height, string version = "89a") => Gif(width, height, version, frames: 1);

    /// <summary>
    /// A GIF: header, logical screen (no global table), a graphic control extension and an image descriptor per frame,
    /// each with LZW code size 2 and one data sub-block, then the trailer. Each frame covers the screen.
    /// </summary>
    public static byte[] Gif(ushort width, ushort height, string version, int frames)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("GIF" + version));
        bytes.AddRange(LittleEndian16(width));
        bytes.AddRange(LittleEndian16(height));
        bytes.AddRange([0x00, 0x00, 0x00]);   // packed (no global table), background, aspect
        for (var i = 0; i < frames; i++)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]);   // graphic control: delay 10, terminator
            bytes.AddRange(GifFrame(0, 0, width, height));
        }

        bytes.Add(0x3B);
        return [.. bytes];
    }

    /// <summary>One image descriptor at (left, top) of the given size, no local table, code size 2 and one data sub-block.</summary>
    public static byte[] GifFrame(ushort left, ushort top, ushort width, ushort height)
    {
        var bytes = new List<byte> { 0x2C };
        bytes.AddRange(LittleEndian16(left));
        bytes.AddRange(LittleEndian16(top));
        bytes.AddRange(LittleEndian16(width));
        bytes.AddRange(LittleEndian16(height));
        bytes.AddRange([0x00, 0x02, 0x02, 0x4C, 0x01, 0x00]);   // packed, code size 2, sub-block of 2, terminator
        return [.. bytes];
    }

    /// <summary>
    /// SOI, fill bytes, an APP0 (JFIF) and a DHT segment the walker must skip, then the frame header.
    /// </summary>
    public static byte[] Jpeg(ushort width, ushort height, byte sof = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange([0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        bytes.AddRange([0xFF, 0xFF, 0xC4, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00]);   // fill byte, then DHT (C4 is not a frame)
        bytes.AddRange([0xFF, sof, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        bytes.AddRange([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>
    /// A JPEG shaped like a camera or encoder's: SOI, optionally an EXIF APP1, APP0 (JFIF), a quantisation table, the frame
    /// header (<paramref name="sof"/>: C0 baseline, C2 progressive), a Huffman table, optionally a restart interval, then
    /// <paramref name="scans"/> scans, each an SOS header and entropy-coded bytes with a stuffed <c>FF 00</c> (and restart
    /// markers when asked), then EOI. Fix round: the structure walk counts the scans.
    /// </summary>
    public static byte[] JpegScans(ushort width, ushort height, int scans, byte sof = 0xC2, bool restart = false, bool exif = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (exif)
        {
            bytes.AddRange([0xFF, 0xE1, 0x00, 0x10]);
            bytes.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
            bytes.AddRange([0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08]);
        }

        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange([0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        bytes.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        bytes.AddRange(Enumerable.Repeat((byte)0x10, 64));
        bytes.AddRange([0xFF, sof, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        bytes.AddRange([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        bytes.AddRange([0xFF, 0xC4, 0x00, 0x14, 0x00]);
        bytes.AddRange(Enumerable.Repeat((byte)0x01, 17));
        if (restart)
        {
            bytes.AddRange([0xFF, 0xDD, 0x00, 0x04, 0x00, 0x10]);
        }

        for (var i = 0; i < scans; i++)
        {
            bytes.AddRange([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
            bytes.AddRange([0x12, 0xFF, 0x00, 0x34, 0x56]);
            if (restart)
            {
                bytes.AddRange([0xFF, (byte)(0xD0 + (i % 8)), 0x78, 0xFF, 0x00, 0x9A]);
            }
        }

        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    public static byte[] WebpLossy(ushort width, ushort height)
    {
        var bytes = Riff("VP8 ", 10);
        bytes[20] = 0x10;   // frame tag: bit 0 clear = keyframe
        bytes[23] = 0x9D;
        bytes[24] = 0x01;
        bytes[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), height);
        return bytes;
    }

    public static byte[] WebpLossless(uint width, uint height)
    {
        var bytes = Riff("VP8L", 5);
        bytes[20] = 0x2F;
        var bits = (width - 1) | ((height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), bits);
        return bytes;
    }

    /// <summary>An extended WebP: a VP8X chunk with the canvas size, then one lossless image of the same size.</summary>
    public static byte[] WebpExtended(uint width, uint height) => WebpExtended(width, height, 0, WebpChunk("VP8L", Vp8l(width, height)));

    /// <summary>An extended WebP: a VP8X chunk with the given flags and canvas size, then the given chunks as they are.</summary>
    public static byte[] WebpExtended(uint width, uint height, byte flags, params byte[][] chunks)
    {
        var vp8x = new byte[10];
        vp8x[0] = flags;
        WriteUInt24(vp8x, 4, width - 1);
        WriteUInt24(vp8x, 7, height - 1);
        var body = new List<byte>(WebpChunk("VP8X", vp8x));
        foreach (var chunk in chunks)
        {
            body.AddRange(chunk);
        }

        return RiffOf([.. body]);
    }

    /// <summary>A RIFF chunk: FourCC, size, the payload and a pad byte when its size is odd.</summary>
    public static byte[] WebpChunk(string fourCc, byte[] payload)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(fourCc));
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        bytes.AddRange(size);
        bytes.AddRange(payload);
        if (payload.Length % 2 == 1)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    /// <summary>A VP8L payload: the signature and the packed sizes.</summary>
    public static byte[] Vp8l(uint width, uint height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), (width - 1) | ((height - 1) << 14));
        return payload;
    }

    /// <summary>"RIFF", the size of what follows, "WEBP" and the chunks.</summary>
    public static byte[] RiffOf(byte[] chunks)
    {
        var bytes = new byte[12 + chunks.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        chunks.CopyTo(bytes, 12);
        return bytes;
    }

    /// <summary>
    /// A cover-sized fixture whose real type is <paramref name="mime"/>.
    /// </summary>
    public static byte[] For(string mime) => mime switch
    {
        "image/png" => Png(CoverSide, CoverSide),
        "image/jpeg" => Jpeg(CoverSide, CoverSide),
        "image/gif" => Gif(CoverSide, CoverSide),
        "image/webp" => WebpLossy(CoverSide, CoverSide),
        _ => throw new ArgumentOutOfRangeException(nameof(mime), mime, "no fixture for this type")
    };

    private static byte[] LittleEndian16(ushort value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] Riff(string chunk, int payload)
    {
        // An odd payload gets its pad byte, which the RIFF size counts.
        var bytes = new byte[20 + payload + (payload % 2)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)payload);
        return bytes;
    }

    private static void WriteUInt24(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
    }
}
