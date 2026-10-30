using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Reads Icecast/Shoutcast in-stream metadata (card L6). With <c>Icy-MetaData: 1</c> a server that supports it answers an
/// <c>icy-metaint</c> header N and then sends N audio bytes, one length byte L, and L × 16 bytes of metadata text such as
/// <c>StreamTitle='Artist - Title';StreamUrl='';</c>, padded with NUL bytes, and again. Pure functions, no I/O. Everything
/// here is station-supplied and untrusted: the bounds are fixed, a title may hold quotes, may lack its terminator and may
/// not be UTF-8.
/// </summary>
public static class IcyMetadata
{
    /// <summary>
    /// Largest <c>icy-metaint</c> accepted: the audio bytes read (and thrown away) before the metadata block. Servers send
    /// 8 192 (Shoutcast) or 16 000 (Icecast); a larger value is refused and the stream counts as sending no titles.
    /// </summary>
    public const int MaxMetaInt = 64 * 1024;

    /// <summary>The metadata length byte counts in units of this many bytes.</summary>
    public const int BlockUnit = 16;

    /// <summary>Largest metadata block: the length byte is at most 255.</summary>
    public const int MaxBlockBytes = byte.MaxValue * BlockUnit;

    /// <summary>Longest title kept, after cleaning (<see cref="StationText.Clean"/>); a longer one is cut with an ellipsis.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The field that carries the title, up to its opening quote. Matched ignoring case.</summary>
    private const string TitleField = "StreamTitle='";

    /// <summary>What ends a field's value: a quote and a semicolon.</summary>
    private const string FieldEnd = "';";

    /// <summary>Strict UTF-8: invalid bytes throw, so the Latin-1 fallback can take over.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Gets the most bytes a sample reads after the headers for a given <c>icy-metaint</c>: the audio, the length byte and
    /// the largest metadata block.
    /// </summary>
    /// <param name="metaInt">The accepted <c>icy-metaint</c>.</param>
    /// <returns>The read cap.</returns>
    public static int ReadCap(int metaInt) => metaInt + 1 + MaxBlockBytes;

    /// <summary>
    /// Parses an <c>icy-metaint</c> header value: a whole number from 1 to <see cref="MaxMetaInt"/>, surrounding spaces
    /// allowed. Anything else (missing, empty, signed, not a number, zero, too large) means no titles are read.
    /// </summary>
    /// <param name="value">The header value as sent, or null when the header is missing.</param>
    /// <param name="metaInt">The accepted value.</param>
    /// <returns>True when the value is accepted.</returns>
    public static bool TryParseMetaInt(string? value, out int metaInt)
    {
        metaInt = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 7
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1 || parsed > MaxMetaInt)
        {
            return false;
        }

        metaInt = parsed;
        return true;
    }

    /// <summary>
    /// Gets the <c>StreamTitle</c> value from one metadata block, as sent (not cleaned). The value runs from the field's
    /// opening quote to the first <c>';</c> that ends the block or starts another field (<c>Name=</c>), so a quote inside
    /// the title (<c>Guns N' Roses</c>, <c>Don';t</c>) is kept. Without a terminator the rest of the block is the value.
    /// </summary>
    /// <param name="block">The metadata block, NUL padding included.</param>
    /// <returns>The title as sent; empty when the field is empty; null when the block has no <c>StreamTitle</c>.</returns>
    public static string? StreamTitle(ReadOnlySpan<byte> block)
    {
        var text = Decode(block.TrimEnd((byte)0));
        var start = text.IndexOf(TitleField, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += TitleField.Length;
        for (var end = text.IndexOf(FieldEnd, start, StringComparison.Ordinal); end >= 0; end = text.IndexOf(FieldEnd, end + 1, StringComparison.Ordinal))
        {
            if (IsFieldBoundary(text, end + FieldEnd.Length))
            {
                return text[start..end];
            }
        }

        // No terminator: the rest, without a closing quote or semicolon left half-sent.
        var rest = text[start..].TrimEnd('\0', ' ', ';');
        return rest.EndsWith('\'') ? rest[..^1] : rest;
    }

    /// <summary>
    /// Decodes metadata bytes: UTF-8 when the bytes are valid UTF-8 (a byte order mark dropped), else Latin-1, which
    /// many older servers send and which maps every byte to a character.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Whether the text at <paramref name="index"/> is the end of the block or the start of another field: spaces, then
    /// either nothing or a name of letters, digits or <c>_</c> followed by <c>=</c>.
    /// </summary>
    private static bool IsFieldBoundary(string text, int index)
    {
        var i = index;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\0'))
        {
            i++;
        }

        if (i == text.Length)
        {
            return true;
        }

        var nameStart = i;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i > nameStart && i < text.Length && text[i] == '=';
    }
}
