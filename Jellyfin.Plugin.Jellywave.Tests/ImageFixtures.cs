using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Minimal image headers built in code, one per format, with the dimensions written where each format keeps them.
/// Only the header is real; the sniffer never looks past it.
/// </summary>
internal static class ImageFixtures
{
    public const int CoverSide = 512;

    public static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;   // bit depth
        bytes[25] = 6;   // RGBA
        return bytes;
    }

    public static byte[] Gif(ushort width, ushort height, string version = "89a")
    {
        var bytes = new byte[13];
        Encoding.ASCII.GetBytes("GIF" + version).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), height);
        return bytes;
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

    public static byte[] WebpExtended(uint width, uint height)
    {
        var bytes = Riff("VP8X", 10);
        WriteUInt24(bytes, 24, width - 1);
        WriteUInt24(bytes, 27, height - 1);
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

    private static byte[] Riff(string chunk, int payload)
    {
        var bytes = new byte[20 + payload];
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
