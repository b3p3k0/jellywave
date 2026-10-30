using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What the add-station probe (card L3) learned from one headers-only request to a stream address: the status, a redirect
/// target, the declared type and the station's <c>icy-*</c> headers as sent (untrusted; <see cref="StationProbe"/> cleans
/// them). <see cref="Body"/> is set only for a playlist answer, read up to <see cref="StationClient.MaxPlaylistBytes"/>.
/// </summary>
public sealed class StreamAnswer
{
    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; init; }

    /// <summary>Gets the redirect target resolved against the request URL, for a 3xx with a <c>Location</c>; otherwise null.</summary>
    public Uri? Location { get; init; }

    /// <summary>Gets the declared media type (no parameters), or null.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the <c>icy-name</c> header as sent, or null.</summary>
    public string? IcyName { get; init; }

    /// <summary>Gets the <c>icy-br</c> header as sent, or null.</summary>
    public string? IcyBitrate { get; init; }

    /// <summary>Gets the <c>icy-sr</c> header as sent, or null.</summary>
    public string? IcySampleRate { get; init; }

    /// <summary>Gets the <c>icy-genre</c> header as sent, or null (card L8).</summary>
    public string? IcyGenre { get; init; }

    /// <summary>Gets the <c>icy-description</c> header as sent, or null (card L8).</summary>
    public string? IcyDescription { get; init; }

    /// <summary>Gets the <c>icy-url</c> header as sent, or null (card L8; never requested, only offered as the website).</summary>
    public string? IcyUrl { get; init; }

    /// <summary>Gets the playlist body (UTF-8), or null when the body was not read.</summary>
    public string? Body { get; init; }

    /// <summary>Gets a value indicating whether the playlist body was larger than the cap (then <see cref="Body"/> is null).</summary>
    public bool BodyTooLarge { get; init; }

    /// <summary>Gets a value indicating whether the status is a redirect (3xx).</summary>
    public bool IsRedirect => Status is >= 300 and < 400;

    /// <summary>Gets a value indicating whether the status is a success (2xx).</summary>
    public bool IsSuccess => Status is >= 200 and < 300;
}
