using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Card R2a (HI decision D9): walks the structure of a PNG, GIF or WebP image and accepts it only when it is one still
/// image. <see cref="ImageSniffer"/> sizes the first frame from the header only, so before this a file within 4 MB and
/// 4096 × 4096 could still carry many frames for Jellyfin's image processor to decode. The walk reads block and chunk
/// headers, never pixel data: GIF blocks (more than one image descriptor is animated), PNG chunks (an <c>acTL</c>,
/// <c>fcTL</c> or <c>fdAT</c> chunk is animated, or contradicts a still image), WebP RIFF chunks (the VP8X animation flag,
/// an <c>ANIM</c> or <c>ANMF</c> chunk is animated). Every step moves forward by at least one byte and every read is
/// bounds-checked, so the walk is linear in the bytes, which the station client caps at 4 MB. Malformed or contradictory
/// structure (a block or chunk running past the end, no end marker, a frame outside its canvas, a second header) is
/// refused like an animation. JPEG (fix round): a progressive JPEG decodes once per scan, so the markers are walked
/// (segments by their length, entropy-coded data byte by byte past stuffed bytes and restart markers) and more than
/// <see cref="MaxJpegScans"/> start-of-scan markers are refused; a segment length below 2 is refused; anything else a
/// decoder tolerates (a file cut short, stray bytes between segments) is accepted, as before.
/// </summary>
internal static class ImageStructure
{
    /// <summary>The most scans (SOS markers) a JPEG may have. Real progressive JPEGs have about ten.</summary>
    internal const int MaxJpegScans = 100;

    private const byte JpegMarkerPrefix = 0xFF;
    private const byte JpegStuffed = 0x00;
    private const byte JpegTem = 0x01;
    private const byte JpegFirstRestart = 0xD0;
    private const byte JpegLastRestart = 0xD7;
    private const byte JpegSoi = 0xD8;
    private const byte JpegEoi = 0xD9;
    private const byte JpegSos = 0xDA;
    private const int JpegMinSegmentLength = 2;

    // GIF: signature (6) and logical screen descriptor (7).
    private const int GifScreenEnd = 13;
    private const int GifPackedOffset = 10;
    private const byte GifTableFlag = 0x80;
    private const int GifTableSizeMask = 0x07;
    private const int GifColorBytes = 3;
    private const byte GifImageSeparator = 0x2C;
    private const byte GifExtensionIntroducer = 0x21;
    private const byte GifTrailer = 0x3B;

    // GIF image descriptor: separator (1), left (2), top (2), width (2), height (2), packed (1).
    private const int GifDescriptorLength = 10;
    private const int GifMinCodeSize = 2;
    private const int GifMaxCodeSize = 8;

    // PNG: signature (8); a chunk is length (4), type (4), data, CRC (4).
    private const int PngSignatureLength = 8;
    private const int PngChunkOverhead = 12;
    private const int PngIhdrLength = 13;

    // RIFF: "RIFF" (4), size (4), "WEBP" (4); a chunk is FourCC (4), size (4), data, a pad byte when the size is odd.
    private const int RiffHeaderLength = 12;
    private const int RiffSizeEnd = 8;
    private const int RiffChunkHeader = 8;
    private const int MinRiffSize = 4;
    private const byte Vp8xAnimationFlag = 0x02;
    private const int Vp8xLength = 10;
    private const int Vp8Length = 10;
    private const int Vp8lLength = 5;
    private const int Vp8SideMask = 0x3FFF;
    private const int Vp8lSideBits = 14;
    private const byte Vp8lSignature = 0x2F;

    private static ReadOnlySpan<byte> Vp8StartCode => [0x9D, 0x01, 0x2A];

    /// <summary>
    /// Says whether an image whose type and header dimensions <see cref="ImageSniffer"/> accepted is one still image with a
    /// well-formed structure.
    /// </summary>
    /// <param name="data">The image bytes.</param>
    /// <param name="mimeType">The sniffed media type.</param>
    /// <returns>True for one still image; false for an animation, a malformed or contradictory structure, or an unknown type.</returns>
    internal static bool IsOneStillImage(ReadOnlySpan<byte> data, string mimeType) => mimeType switch
    {
        ImageSniffer.Jpeg => HasFewScans(data),
        ImageSniffer.Png => IsStillPng(data),
        ImageSniffer.Gif => IsStillGif(data),
        ImageSniffer.Webp => IsStillWebp(data),
        _ => false
    };

    /// <summary>
    /// Walks every block from the logical screen descriptor to the trailer: exactly one image descriptor, inside the
    /// logical screen, with a valid LZW code size; extensions and image data are skipped sub-block by sub-block.
    /// </summary>
    private static bool IsStillGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < GifScreenEnd)
        {
            return false;
        }

        long screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        long screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        long p = GifScreenEnd + ColorTableBytes(data[GifPackedOffset]);
        var images = 0;
        while (p < data.Length)
        {
            var block = data[(int)p];
            if (block == GifTrailer)
            {
                return images == 1;
            }

            if (block == GifExtensionIntroducer)
            {
                // Introducer and label, then the sub-blocks (a graphic control or a loop count is fine on one frame).
                p += 2;
                if (!SkipSubBlocks(data, ref p))
                {
                    return false;
                }

                continue;
            }

            if (block != GifImageSeparator || data.Length - p < GifDescriptorLength + 1 || ++images > 1)
            {
                // An unknown block, a cut descriptor, or a second frame.
                return false;
            }

            var descriptor = data.Slice((int)p, GifDescriptorLength);
            long left = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(1, 2));
            long top = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(3, 2));
            long width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(5, 2));
            long height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(7, 2));
            if (width == 0 || height == 0 || left + width > screenWidth || top + height > screenHeight)
            {
                // A frame the screen size does not bound: the size check would not have seen it.
                return false;
            }

            p += GifDescriptorLength + ColorTableBytes(descriptor[9]);
            if (p >= data.Length || data[(int)p] is < GifMinCodeSize or > GifMaxCodeSize)
            {
                return false;
            }

            p++;
            if (!SkipSubBlocks(data, ref p))
            {
                return false;
            }
        }

        // No trailer.
        return false;
    }

    /// <summary>
    /// Walks a JPEG's markers after SOI and counts its scans. Segments are skipped by their length; after a start of scan
    /// the entropy-coded data is read up to the next marker (<c>FF 00</c> is a stuffed byte, <c>FF D0</c>–<c>FF D7</c> a
    /// restart marker, <c>FF FF</c> a fill byte). Every step moves forward, so the walk is linear in the bytes.
    /// </summary>
    private static bool HasFewScans(ReadOnlySpan<byte> data)
    {
        long p = 2;
        var scans = 0;
        while (p < data.Length)
        {
            if (data[(int)p] != JpegMarkerPrefix)
            {
                // Stray bytes between segments: decoders skip them to the next marker, and so does the walk.
                p++;
                continue;
            }

            while (p < data.Length && data[(int)p] == JpegMarkerPrefix)
            {
                p++;
            }

            if (p >= data.Length)
            {
                break;
            }

            var marker = data[(int)p++];
            if (marker == JpegEoi)
            {
                return true;
            }

            if (marker is JpegStuffed or JpegTem or JpegSoi or (>= JpegFirstRestart and <= JpegLastRestart))
            {
                continue;
            }

            if (data.Length - p < 2)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice((int)p, 2));
            if (length < JpegMinSegmentLength)
            {
                return false;
            }

            p += length;
            if (marker != JpegSos)
            {
                continue;
            }

            if (++scans > MaxJpegScans)
            {
                return false;
            }

            p = EndOfEntropyData(data, p);
        }

        // Cut short: what is there decodes, and its scans were counted.
        return true;
    }

    /// <summary>The offset of the marker that ends a scan's entropy-coded data, or the end of the bytes.</summary>
    private static long EndOfEntropyData(ReadOnlySpan<byte> data, long p)
    {
        while (p < data.Length - 1)
        {
            if (data[(int)p] != JpegMarkerPrefix)
            {
                p++;
                continue;
            }

            var next = data[(int)p + 1];
            if (next == JpegStuffed || next is >= JpegFirstRestart and <= JpegLastRestart)
            {
                p += 2;
            }
            else if (next == JpegMarkerPrefix)
            {
                p++;
            }
            else
            {
                return p;
            }
        }

        return data.Length;
    }

    /// <summary>The size of a GIF colour table its packed byte announces, or 0 when it announces none.</summary>
    private static long ColorTableBytes(byte packed)
        => (packed & GifTableFlag) == 0 ? 0 : GifColorBytes * (2L << (packed & GifTableSizeMask));

    /// <summary>Skips GIF data sub-blocks up to and including the zero-length terminator.</summary>
    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref long p)
    {
        while (p < data.Length)
        {
            var size = data[(int)p];
            p += 1 + size;
            if (size == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks every chunk from the signature to <c>IEND</c>: <c>IHDR</c> first and once, at least one <c>IDAT</c>, no APNG
    /// chunk anywhere, and no chunk running past the end. Bytes after <c>IEND</c> are not read.
    /// </summary>
    private static bool IsStillPng(ReadOnlySpan<byte> data)
    {
        long p = PngSignatureLength;
        var first = true;
        var idat = false;
        while (data.Length - p >= PngChunkOverhead)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)p, 4));
            var type = data.Slice((int)p + 4, 4);
            if (length > int.MaxValue || length > data.Length - p - PngChunkOverhead || !IsChunkName(type))
            {
                return false;
            }

            var ihdr = type.SequenceEqual("IHDR"u8);
            if (first != ihdr || (ihdr && length != PngIhdrLength))
            {
                // IHDR must be the first chunk, exactly once, with its fixed length.
                return false;
            }

            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
            {
                // acTL before IDAT makes an APNG; anywhere else it, and any frame chunk, contradicts a still image.
                return false;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return idat;
            }

            idat |= type.SequenceEqual("IDAT"u8);
            first = false;
            p += PngChunkOverhead + length;
        }

        // No IEND, or a chunk header cut short.
        return false;
    }

    private static bool IsChunkName(ReadOnlySpan<byte> type)
    {
        foreach (var c in type)
        {
            if (!char.IsAsciiLetter((char)c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks every RIFF chunk inside the declared RIFF size: a simple file (<c>VP8 </c> or <c>VP8L</c> first) holds that
    /// one image chunk and nothing that belongs to the extended format; an extended file (<c>VP8X</c> first) has the
    /// animation flag clear, no <c>ANIM</c> or <c>ANMF</c> chunk, and exactly one image chunk of the canvas size.
    /// </summary>
    private static bool IsStillWebp(ReadOnlySpan<byte> data)
    {
        if (data.Length < RiffHeaderLength)
        {
            return false;
        }

        long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (riffSize < MinRiffSize || riffSize > data.Length - RiffSizeEnd)
        {
            // Declared larger than the bytes there: cut short.
            return false;
        }

        var end = RiffSizeEnd + riffSize;
        long p = RiffHeaderLength;
        var extended = false;
        long canvasWidth = 0;
        long canvasHeight = 0;
        var images = 0;
        while (p < end)
        {
            if (end - p < RiffChunkHeader)
            {
                return false;
            }

            var fourCc = data.Slice((int)p, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)p + 4, 4));
            var padded = size + (size & 1);
            if (padded > end - p - RiffChunkHeader)
            {
                return false;
            }

            var payload = data.Slice((int)p + RiffChunkHeader, (int)size);
            var isFirst = p == RiffHeaderLength;
            p += RiffChunkHeader + padded;

            if (fourCc.SequenceEqual("VP8X"u8))
            {
                if (!isFirst || size < Vp8xLength || (payload[0] & Vp8xAnimationFlag) != 0)
                {
                    // Only first, and not animated.
                    return false;
                }

                extended = true;
                canvasWidth = ReadUInt24LittleEndian(payload.Slice(4, 3)) + 1;
                canvasHeight = ReadUInt24LittleEndian(payload.Slice(7, 3)) + 1;
                continue;
            }

            if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8))
            {
                return false;
            }

            var lossy = fourCc.SequenceEqual("VP8 "u8);
            if (lossy || fourCc.SequenceEqual("VP8L"u8))
            {
                if (++images > 1 || !TryReadBitstreamSize(payload, lossy, out var width, out var height))
                {
                    return false;
                }

                if (extended && (width != canvasWidth || height != canvasHeight))
                {
                    // A still image's bitstream is the canvas; another size contradicts the header the size check read.
                    return false;
                }

                continue;
            }

            if (isFirst || (!extended && fourCc.SequenceEqual("ALPH"u8)))
            {
                // The first chunk must say which format this is; an alpha chunk belongs to the extended format only.
                return false;
            }
        }

        return images == 1;
    }

    /// <summary>Reads the size of a VP8 key frame or a VP8L image from its chunk payload.</summary>
    private static bool TryReadBitstreamSize(ReadOnlySpan<byte> payload, bool lossy, out long width, out long height)
    {
        width = 0;
        height = 0;
        if (lossy)
        {
            if (payload.Length < Vp8Length || (payload[0] & 1) != 0 || !payload.Slice(3, 3).SequenceEqual(Vp8StartCode))
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & Vp8SideMask;
            height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & Vp8SideMask;
            return true;
        }

        if (payload.Length < Vp8lLength || payload[0] != Vp8lSignature)
        {
            return false;
        }

        var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        width = (bits & Vp8SideMask) + 1;
        height = ((bits >> Vp8lSideBits) & Vp8SideMask) + 1;
        return true;
    }

    private static long ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
