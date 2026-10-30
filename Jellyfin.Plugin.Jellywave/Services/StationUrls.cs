using System;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Builds the station URLs from configuration. Pure functions, no I/O. The overloads that take a
/// <see cref="PluginConfiguration"/> read its <see cref="PluginConfiguration.PrimaryStation"/> (the channel's description
/// and home page; the poller and the station routes take each station's own URLs since cards L4 and L5).
/// </summary>
public static class StationUrls
{
    /// <summary>Refusal: the value does not parse as an absolute URL.</summary>
    public const string RefusedUnparsable = "it is not an absolute URL";

    /// <summary>Refusal: the scheme is not http or https.</summary>
    public const string RefusedScheme = "only http:// and https:// are allowed";

    /// <summary>Refusal: the value carries userinfo (the userinfo itself is never repeated).</summary>
    public const string RefusedCredentials = "it contains credentials (user:password@)";

    /// <summary>Refusal: the value carries a query.</summary>
    public const string RefusedQuery = "it contains a query (?…)";

    /// <summary>Refusal: the value carries a fragment.</summary>
    public const string RefusedFragment = "it contains a fragment (#…)";

    /// <summary>Refusal: the value has no host.</summary>
    public const string RefusedHost = "it has no host name";

    /// <summary>Refusal: a website longer than <see cref="MaxWebsiteLength"/> characters.</summary>
    public const string RefusedTooLong = "it is longer than 2048 characters";

    /// <summary>Longest website link the server serves (card L8), as typed and as escaped.</summary>
    public const int MaxWebsiteLength = 2048;

    /// <summary>What an <c>icy-url</c> without a scheme gets in front (card L8).</summary>
    private const string IcyUrlDefaultScheme = "http://";

    private const string SchemeDelimiter = "://";

    /// <summary>
    /// Tries to build the station base URL with a trailing slash. A path prefix is kept
    /// (for example <c>http://host/radio</c> becomes <c>http://host/radio/</c>).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="baseUrl">The base URL, or null when unset, not an absolute http(s) URL, or carrying credentials, a query or a fragment.</param>
    /// <returns>True when the base URL is usable.</returns>
    public static bool TryGetBaseUrl(PluginConfiguration config, out Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TryParseBaseUrl(config.PrimaryStation()?.BaseUrl, out baseUrl);
    }

    /// <summary>
    /// Gets the station origin that keys everything cached from the station (now-playing state, schedule, applied
    /// cover): the parsed base URL in full, so a change of host, port, scheme or path prefix counts as another station.
    /// </summary>
    /// <param name="baseUrl">The base URL from <see cref="TryGetBaseUrl"/>.</param>
    /// <returns>The origin key.</returns>
    public static string Origin(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return baseUrl.AbsoluteUri;
    }

    /// <summary>
    /// Parses a station base URL string the way <see cref="TryGetBaseUrl"/> does.
    /// </summary>
    /// <param name="rawBaseUrl">The raw value, possibly null or padded.</param>
    /// <param name="baseUrl">The base URL with a trailing slash, or null.</param>
    /// <returns>
    /// True when the value is an absolute http(s) URL with no userinfo (not even an empty <c>@</c>), no query and no
    /// fragment. HTTP is allowed: the station normally runs on plain HTTP on the LAN.
    /// </returns>
    public static bool TryParseBaseUrl(string? rawBaseUrl, out Uri? baseUrl)
        => CheckBaseUrl(rawBaseUrl, out baseUrl) is null && baseUrl is not null;

    /// <summary>
    /// Says why a non-empty station base URL is refused, so a saved value that no longer passes is not mistaken
    /// for "not configured". The reason is a fixed sentence and never repeats any part of the value.
    /// </summary>
    /// <param name="rawBaseUrl">The raw value, possibly null or padded.</param>
    /// <returns>
    /// Null when the value is blank (not configured) or usable; otherwise one of <see cref="RefusedUnparsable"/>,
    /// <see cref="RefusedScheme"/>, <see cref="RefusedCredentials"/>, <see cref="RefusedQuery"/> or <see cref="RefusedFragment"/>.
    /// </returns>
    public static string? BaseUrlRefusal(string? rawBaseUrl)
        => CheckBaseUrl(rawBaseUrl, out _) is { Length: > 0 } reason ? reason : null;

    /// <summary>
    /// Tries to build the primary station's server stream URL (see the <see cref="StationDefinition"/> overload).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="url">The absolute stream URL, or null when the configuration is incomplete or escapes the base.</param>
    /// <returns>True when a usable URL was built.</returns>
    public static bool TryGetServerStreamUrl(PluginConfiguration config, out Uri? url)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TryGetServerStreamUrl(config.PrimaryStation(), out url);
    }

    /// <summary>
    /// Tries to build the stream URL the Jellyfin server itself uses (relay origin, ffmpeg input) for a SUB/WAVE
    /// station: the base URL plus <see cref="StationDefinition.StreamPath"/>, which is always resolved
    /// under the base URL's path, on the same scheme, host, port and userinfo. A stream path that is itself an
    /// absolute URI (it has a scheme) is refused.
    /// </summary>
    /// <param name="station">The station, or null.</param>
    /// <param name="url">The absolute stream URL, or null when the station is missing, incomplete or escapes the base.</param>
    /// <returns>True when a usable URL was built.</returns>
    public static bool TryGetServerStreamUrl(StationDefinition? station, out Uri? url)
    {
        url = null;

        if (station is null || !TryParseBaseUrl(station.BaseUrl, out var baseUrl) || baseUrl is null)
        {
            return false;
        }

        // Relative to the base: "/stream.mp3", "stream.mp3" and "//stream.mp3" all mean <base>/stream.mp3.
        var relative = (station.StreamPath ?? string.Empty).Trim().TrimStart('/');
        if (relative.Length == 0 || Array.Exists(relative.Split('/'), segment => segment == "..") || Uri.TryCreate(relative, UriKind.Absolute, out _))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, relative, out var built)
            || !IsHttp(built)
            // Load-bearing, not a backstop: "\/host:8000/x" is no absolute URI on Linux, yet resolves to the same host on another port.
            || Uri.Compare(built, baseUrl, UriComponents.SchemeAndServer | UriComponents.UserInfo, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0
            || !built.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        url = built;
        return true;
    }

    /// <summary>
    /// Parses a whole plain-stream URL (<see cref="StationKind.Stream"/>, card L1; the item's URL from L2 on).
    /// </summary>
    /// <param name="rawStreamUrl">The raw value, possibly null or padded.</param>
    /// <param name="url">The URL, or null when refused or blank.</param>
    /// <returns>
    /// True when the value is an absolute http(s) URL with a host, no userinfo (not even an empty <c>@</c>) and no
    /// fragment (not even a bare <c>#</c>). A query is allowed (HI decision): some stream URLs need one.
    /// </returns>
    public static bool TryParseStreamUrl(string? rawStreamUrl, out Uri? url)
        => CheckStreamUrl(rawStreamUrl, out url) is null && url is not null;

    /// <summary>
    /// Parses a plain stream's logo URL (card L2) under the same whole-URL rule as <see cref="TryParseStreamUrl"/>:
    /// absolute http(s) with a host, no userinfo, no fragment, a query allowed.
    /// </summary>
    /// <param name="rawLogoUrl">The raw value, possibly null or padded.</param>
    /// <param name="url">The URL, or null when refused or blank.</param>
    /// <returns>True when the value is usable.</returns>
    public static bool TryParseLogoUrl(string? rawLogoUrl, out Uri? url)
        => CheckStreamUrl(rawLogoUrl, out url) is null && url is not null;

    /// <summary>
    /// Says why a non-empty plain-stream URL is refused. The reason is a fixed sentence and never repeats any part of
    /// the value.
    /// </summary>
    /// <param name="rawStreamUrl">The raw value, possibly null or padded.</param>
    /// <returns>
    /// Null when the value is blank or usable; otherwise one of <see cref="RefusedUnparsable"/>, <see cref="RefusedScheme"/>,
    /// <see cref="RefusedHost"/>, <see cref="RefusedCredentials"/> or <see cref="RefusedFragment"/>.
    /// </returns>
    public static string? StreamUrlRefusal(string? rawStreamUrl)
        => CheckStreamUrl(rawStreamUrl, out _) is { Length: > 0 } reason ? reason : null;

    /// <summary>
    /// The website rule (card L8): a station's website as the station page may link it. Absolute <c>http</c> or
    /// <c>https</c> with a host that is ASCII once converted to IDN (served as punycode, so a look-alike Unicode host
    /// shows as <c>xn--…</c>), no userinfo (not even an empty <c>@</c>), at most <see cref="MaxWebsiteLength"/>
    /// characters as typed and as escaped; a query and a fragment are allowed (it is a link, never requested by the
    /// server). Anything else is dropped where it is read; the station stays listed.
    /// </summary>
    /// <param name="rawWebsite">The raw value, possibly null or padded.</param>
    /// <returns>The escaped link (<see cref="Uri.AbsoluteUri"/>), or null when blank or refused.</returns>
    public static string? WebsiteLink(string? rawWebsite)
        => CheckWebsite(rawWebsite, out var url) is null && url is not null ? url.AbsoluteUri : null;

    /// <summary>
    /// Says why a non-empty website is refused. The reason is a fixed sentence and never repeats any part of the value.
    /// </summary>
    /// <param name="rawWebsite">The raw value, possibly null or padded.</param>
    /// <returns>
    /// Null when the value is blank or usable; otherwise one of <see cref="RefusedUnparsable"/>, <see cref="RefusedScheme"/>,
    /// <see cref="RefusedHost"/>, <see cref="RefusedCredentials"/> or <see cref="RefusedTooLong"/>.
    /// </returns>
    public static string? WebsiteRefusal(string? rawWebsite)
        => CheckWebsite(rawWebsite, out _) is { Length: > 0 } reason ? reason : null;

    /// <summary>
    /// Turns a stream's <c>icy-url</c> header (the probe, card L8) into a website link: a value without <c>://</c> gets
    /// <c>http://</c> in front (stations often send a bare host such as <c>example.org</c>), then the website rule. A
    /// value with another scheme (<c>javascript:</c>, <c>data:</c>) does not parse as an http host once prefixed, so it
    /// is refused like any other.
    /// </summary>
    /// <param name="icyUrl">The header value, already cleaned of control characters.</param>
    /// <returns>The escaped link, or null.</returns>
    public static string? WebsiteFromIcyUrl(string? icyUrl)
    {
        var raw = icyUrl?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return WebsiteLink(raw.Contains(SchemeDelimiter, StringComparison.Ordinal) ? raw : IcyUrlDefaultScheme + raw);
    }

    /// <summary>
    /// The one base URL rule. Returns null when usable (with <paramref name="baseUrl"/> set), the empty string when
    /// blank, or the refusal reason.
    /// </summary>
    private static string? CheckBaseUrl(string? rawBaseUrl, out Uri? baseUrl)
    {
        baseUrl = null;

        var raw = rawBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // Check the value as typed, before the slash is appended: "http://host/radio?x=1" would otherwise carry "?x=1/".
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var typed))
        {
            return RefusedUnparsable;
        }

        if (!IsHttp(typed))
        {
            return RefusedScheme;
        }

        if (HasUserInfo(typed))
        {
            return RefusedCredentials;
        }

        if (typed.Query.Length > 0)
        {
            return RefusedQuery;
        }

        if (typed.Fragment.Length > 0)
        {
            return RefusedFragment;
        }

        if (!Uri.TryCreate(raw + "/", UriKind.Absolute, out var parsed) || !IsHttp(parsed))
        {
            return RefusedUnparsable;
        }

        baseUrl = parsed;
        return null;
    }

    /// <summary>
    /// The one plain-stream URL rule. Returns null when usable (with <paramref name="url"/> set), the empty string when
    /// blank, or the refusal reason.
    /// </summary>
    private static string? CheckStreamUrl(string? rawStreamUrl, out Uri? url)
    {
        url = null;

        var raw = rawStreamUrl?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            return RefusedUnparsable;
        }

        if (!IsHttp(parsed))
        {
            return RefusedScheme;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return RefusedHost;
        }

        if (HasUserInfo(parsed))
        {
            return RefusedCredentials;
        }

        // The raw check catches a bare "#" too, whatever Uri makes of an empty fragment; '#' has no other place in a URL.
        if (parsed.Fragment.Length > 0 || raw.Contains('#', StringComparison.Ordinal))
        {
            return RefusedFragment;
        }

        url = parsed;
        return null;
    }

    /// <summary>
    /// The one website rule. Returns null when usable (with <paramref name="url"/> set), the empty string when blank, or
    /// the refusal reason.
    /// </summary>
    private static string? CheckWebsite(string? rawWebsite, out Uri? url)
    {
        url = null;

        var raw = rawWebsite?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (raw.Length > MaxWebsiteLength)
        {
            return RefusedTooLong;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            return RefusedUnparsable;
        }

        if (!IsHttp(parsed))
        {
            return RefusedScheme;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return RefusedHost;
        }

        if (HasUserInfo(parsed))
        {
            return RefusedCredentials;
        }

        // Serve the host as its IDN (punycode) form, so a look-alike Unicode host shows as xn--… in the link text.
        // IdnHost throws on a host IDNA refuses (U+2028, other disallowed code points).
        if (!TryIdnHost(parsed, out var idnHost))
        {
            return RefusedHost;
        }

        if (!string.Equals(idnHost, parsed.Host, StringComparison.Ordinal))
        {
            parsed = new UriBuilder(parsed) { Host = idnHost }.Uri;
        }

        // Escaping can make the served link longer than what was typed.
        if (parsed.AbsoluteUri.Length > MaxWebsiteLength)
        {
            return RefusedTooLong;
        }

        url = parsed;
        return null;
    }

    private static bool TryIdnHost(Uri uri, out string idnHost)
    {
        idnHost = string.Empty;
        try
        {
            idnHost = uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return false;
        }

        // A DNS name served must be ASCII after IDNA; an IP literal is ASCII already.
        return idnHost.Length > 0 && (uri.HostNameType != UriHostNameType.Dns || idnHost.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'));
    }

    private static bool IsHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // With the delimiter, so "http://@host/" counts too (its UserInfo is empty, but the "@" stays in the URL).
    private static bool HasUserInfo(Uri uri)
        => uri.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter, UriFormat.UriEscaped).Length > 0;
}
