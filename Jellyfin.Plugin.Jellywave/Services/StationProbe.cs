using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The "Check" button of the configuration page's station dialog (card L3, PLAN diagram 10). Checks a pasted stream
/// address from the server: the whole-URL rule, one headers-only request through <see cref="StationClient"/> (a redirect
/// is reported, never followed; a playlist is read and its first entry checked once more), the audio type, and whether a
/// SUB/WAVE station answers <c>/api/state</c> at the address's origin or under its path. Every failure is a fixed sentence;
/// station text (<c>icy-*</c> headers, the station's name and tagline) is cleaned like the now-playing text.
/// </summary>
public sealed class StationProbe
{
    /// <summary>Refusal: the path names a DASH manifest (as in <see cref="StationSources"/>).</summary>
    public const string RefusedDash = "it is a DASH manifest (.mpd), which Jellywave does not serve";

    /// <summary>Line: the address did not answer with an audio stream and no SUB/WAVE station answered there.</summary>
    public const string NotAudio = "not an audio stream: the address answered, but not with MP3, AAC, HLS or a playlist";

    /// <summary>Line: the transport timed out (one call's 8 s, or the probe's whole budget).</summary>
    public const string TimedOut = "not reachable: timed out";

    /// <summary>Line: the connection failed.</summary>
    public const string CouldNotConnect = "not reachable: could not connect";

    /// <summary>Line: the host name did not resolve.</summary>
    public const string NameNotFound = "not reachable: the host name was not found";

    /// <summary>Line: the TLS (https) handshake failed.</summary>
    public const string TlsFailed = "not reachable: the https connection could not be secured";

    /// <summary>Line: the server closed the connection before it answered in full.</summary>
    public const string ConnectionClosed = "the server closed the connection before it answered";

    /// <summary>Line: the server answered with an "ICY 200 OK" status line (Shoutcast v1), which .NET's HTTP client refuses.</summary>
    public const string ShoutcastV1 = "the server answered in the old Shoutcast format (ICY), which Jellyfin cannot read; try the station's Shoutcast v2 or Icecast http:// address";

    /// <summary>Line: the server's answer was not valid HTTP for another reason.</summary>
    public const string InvalidResponse = "the server's answer is not valid HTTP";

    /// <summary>Line: the connection broke while the answer was read.</summary>
    public const string ReadFailed = "the answer could not be read; the connection broke";

    /// <summary>Line: anything else.</summary>
    public const string Unexpected = "unexpected error; see the server log";

    /// <summary>Line: a redirect without a usable target.</summary>
    public const string RedirectRefused = "the address redirected to an address the plugin does not accept";

    /// <summary>Line: the playlist was over the cap.</summary>
    public const string PlaylistTooLarge = "the playlist is larger than 256 KB, so it is not read";

    /// <summary>Line: no http(s) entry in the playlist.</summary>
    public const string PlaylistEmpty = "the playlist lists no http:// or https:// stream address";

    /// <summary>Line prefix: the playlist's first entry fails the whole-URL rule (the fixed reason follows).</summary>
    public const string PlaylistEntryRefused = "the playlist's first stream address is not allowed: ";

    /// <summary>Line: the playlist's entry is itself a playlist; the probe does not go deeper.</summary>
    public const string NestedPlaylist = "the playlist points to another playlist; open it and paste one of its stream addresses";

    /// <summary>Line: an HLS stream.</summary>
    public const string HlsWarning = "HLS: codec assumed AAC (change it under Advanced if the station says otherwise)";

    /// <summary>Line: the SUB/WAVE station requires listener sign-in.</summary>
    public const string ListenerAuthWarning = "the station requires listener sign-in, which Jellywave cannot do: the stream will not play";

    /// <summary>
    /// Line: no SUB/WAVE station answered. Card L11: an ordinary stream reads as one, without naming SUB/WAVE (which a
    /// newcomer read as a failure) and without "added" (nothing is added before Add to list and Save).
    /// </summary>
    public const string PlainStream = "Radio stream.";

    /// <summary>Line: the SUB/WAVE check ran out of time (named, since a SUB/WAVE station may be behind it).</summary>
    public const string DetectionTimedOut = "Radio stream. The SUB/WAVE check did not finish in time; press Check again if this is a SUB/WAVE station.";

    /// <summary>Line: a SUB/WAVE station whose stream address cannot be expressed under its base URL.</summary>
    public const string SubWaveOtherServer = "Radio stream: a SUB/WAVE station answers here, but its stream is not under its address.";

    /// <summary>Line prefix: the SUB/WAVE station's own stream redirected (the stream URL, then the target or a refusal, follow).</summary>
    public const string MountRedirectPrefix = "the station's stream at ";

    /// <summary>Line: a SUB/WAVE station that names no usable stream.</summary>
    public const string SubWaveNoStream = "a SUB/WAVE station answers here, but its stream address is not usable";

    /// <summary>Kind value of a SUB/WAVE station.</summary>
    public const string KindSubWave = nameof(StationKind.SubWave);

    /// <summary>Kind value of a plain stream.</summary>
    public const string KindStream = nameof(StationKind.Stream);

    private const string Mp3 = "mp3";
    private const string Aac = "aac";
    private const string HlsExtension = ".m3u8";
    private const string DashExtension = ".mpd";
    private const string PlsFileKey = "file";
    private const int MinBitrateKbps = 8;
    private const int MaxBitrateKbps = 1536;
    private const int MinSampleRate = 8000;
    private const int MaxSampleRate = 192000;
    private const int MinChannels = 1;
    private const int MaxChannels = 8;
    private const int HertzPerKilohertz = 1000;

    /// <summary>
    /// Whole budget of one probe. Each call already has its 8 s (<see cref="StationClient"/>; a playlist's body has its own
    /// 8 s after its headers); a probe makes at most eight requests (the address, the playlist entry, up to four
    /// <c>/api/state</c> reads, the feed, the mount), so this keeps the dialog's wait under a reverse proxy's usual 60 s.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(30);

    private static readonly string[] PlaylistExtensions = [".pls", ".m3u"];

    private static readonly string[] PlaylistTypes = ["audio/x-scpls", "audio/x-mpegurl", "audio/mpegurl"];

    private static readonly string[] HlsTypes = ["application/vnd.apple.mpegurl", "application/x-mpegurl"];

    private static readonly string[] Mp3Types = ["audio/mpeg"];

    private static readonly string[] AacTypes = ["audio/aac", "audio/aacp"];

    private readonly StationClient _client;
    private readonly ILogger _logger;
    private readonly TimeSpan _budget;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationProbe"/> class.
    /// </summary>
    /// <param name="client">The station client (the no-redirect client and its bounds).</param>
    /// <param name="logger">The logger.</param>
    public StationProbe(StationClient client, ILogger logger)
        : this(client, logger, ProbeBudget)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationProbe"/> class with another budget (tests).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="budget">The whole probe's budget.</param>
    internal StationProbe(StationClient client, ILogger logger, TimeSpan budget)
    {
        _client = client;
        _logger = logger;
        _budget = budget;
    }

    /// <summary>
    /// Checks an address.
    /// </summary>
    /// <param name="rawUrl">The address as pasted.</param>
    /// <param name="cancellationToken">The request's token; when it is cancelled the probe stops and rethrows.</param>
    /// <returns>What was learned, with one line per check.</returns>
    public Task<StationProbeResult> ProbeAsync(string? rawUrl, CancellationToken cancellationToken)
        => ProbeAsync(rawUrl, null, cancellationToken);

    /// <summary>
    /// Checks an address, also trying a SUB/WAVE base URL the station already has (editing a SUB/WAVE station, so a stream
    /// path nested below the base, or a slow origin, does not turn it into a plain stream).
    /// </summary>
    /// <param name="rawUrl">The address as pasted.</param>
    /// <param name="knownBaseUrl">The stored base URL, or null. Used only when it passes the base URL rule and the address
    /// lies under it (same scheme, host and port, path below); otherwise ignored.</param>
    /// <param name="cancellationToken">The request's token; when it is cancelled the probe stops and rethrows.</param>
    /// <returns>What was learned, with one line per check.</returns>
    public async Task<StationProbeResult> ProbeAsync(string? rawUrl, string? knownBaseUrl, CancellationToken cancellationToken)
    {
        var result = new StationProbeResult();
        if (Refusal(rawUrl, out var url) is { } refusal)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, "URL not allowed: " + refusal));
            return result;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);
        try
        {
            await CheckAsync(url!, KnownBase(knownBaseUrl), result, budget.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var category = Category(ex);
            if (category == Unexpected)
            {
                _logger.LogError(ex, "Station probe of {Host} failed", url!.Host);
            }
            else
            {
                _logger.LogInformation("Station probe of {Host}: {Category}", url!.Host, category);
            }

            result.Ok = false;
            result.Kind = null;
            result.Lines.Add(new ProbeLine(ProbeLine.Error, category));
        }

        return result;
    }

    /// <summary>
    /// The whole-URL rule (<see cref="StationUrls.TryParseStreamUrl"/>: http or https, a host, no userinfo, no fragment, a
    /// query allowed) plus the DASH refusal of <see cref="StationSources"/>. A playlist path is allowed here: the probe reads it.
    /// </summary>
    /// <param name="rawUrl">The address as pasted.</param>
    /// <param name="url">The parsed address, or null.</param>
    /// <returns>Null when allowed, else the fixed reason.</returns>
    internal static string? Refusal(string? rawUrl, out Uri? url)
    {
        if (!StationUrls.TryParseStreamUrl(rawUrl, out url) || url is null)
        {
            url = null;
            return StationUrls.StreamUrlRefusal(rawUrl) ?? "no address given";
        }

        if (url.AbsolutePath.EndsWith(DashExtension, StringComparison.OrdinalIgnoreCase))
        {
            url = null;
            return RefusedDash;
        }

        return null;
    }

    /// <summary>
    /// Whether an answer is HLS: a <c>.m3u8</c> path (as <see cref="StationSources"/> and Jellyfin's <c>M3UTunerHost</c> read
    /// it) or an HLS media type. Checked before <see cref="IsPlaylist"/>: HLS is a stream, not a playlist to resolve.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="contentType">The declared media type, or null.</param>
    /// <returns>True for HLS.</returns>
    internal static bool IsHls(Uri url, string? contentType)
        => url.AbsolutePath.EndsWith(HlsExtension, StringComparison.OrdinalIgnoreCase) || Is(contentType, HlsTypes);

    /// <summary>
    /// Whether an answer is a playlist to resolve: a <c>.pls</c> or <c>.m3u</c> path (ignoring case) or a playlist media
    /// type, and not HLS.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="contentType">The declared media type, or null.</param>
    /// <returns>True for a playlist.</returns>
    internal static bool IsPlaylist(Uri url, string? contentType)
        => !IsHls(url, contentType)
            && (Array.Exists(PlaylistExtensions, e => url.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || Is(contentType, PlaylistTypes));

    /// <summary>
    /// The first absolute http(s) entry of a <c>.pls</c> or <c>.m3u</c> body: blank lines, comments (<c>#</c>), section
    /// headers (<c>[playlist]</c>) and relative entries are skipped; in a <c>.pls</c> only <c>FileN=</c> values count.
    /// </summary>
    /// <param name="body">The playlist text.</param>
    /// <returns>The entry as written, or null.</returns>
    internal static string? FirstEntry(string body)
    {
        foreach (var raw in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] is '#' or '[')
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && line[..equals].All(char.IsAsciiLetterOrDigit))
            {
                // A .pls key: only FileN= names a stream; Title1=, Length1=, NumberOfEntries=, Version= do not.
                var key = line[..equals].TrimEnd("0123456789".ToCharArray());
                if (!string.Equals(key, PlsFileKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                line = line[(equals + 1)..].Trim();
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var entry) && (entry.Scheme == Uri.UriSchemeHttp || entry.Scheme == Uri.UriSchemeHttps))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Cleans station text for the page: control characters dropped, whitespace collapsed, at most
    /// <see cref="NowPlayingState.MaxFieldLength"/> characters (as the now-playing text).
    /// </summary>
    /// <param name="value">The text as sent.</param>
    /// <returns>The cleaned text, or null when nothing is left.</returns>
    internal static string? Clean(string? value) => StationText.Clean(value, NowPlayingState.MaxFieldLength);

    /// <summary>
    /// The leading whole number of an <c>icy-*</c> value ("128", "128,128", " 44100 "), within the bounds; else null.
    /// </summary>
    /// <param name="value">The header value as sent.</param>
    /// <param name="min">The smallest accepted number.</param>
    /// <param name="max">The largest accepted number.</param>
    /// <returns>The number, or null.</returns>
    internal static int? LeadingNumber(string? value, int min, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        var digits = text.TakeWhile(char.IsAsciiDigit).Count();
        return digits is > 0 and <= 7 && int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= min && number <= max
            ? number
            : null;
    }

    private static bool Is(string? contentType, string[] types)
        => contentType is not null && Array.Exists(types, t => string.Equals(t, contentType.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Category(Exception ex)
        => ex switch
        {
            OperationCanceledException => TimedOut,
            HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse } invalid
                => invalid.Message.Contains("'ICY ", StringComparison.Ordinal) ? ShoutcastV1 : InvalidResponse,
            HttpRequestException { StatusCode: { } status } => string.Create(CultureInfo.InvariantCulture, $"not reachable: HTTP {(int)status}"),
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => NameNotFound,
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => TlsFailed,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => ConnectionClosed,
            HttpRequestException => CouldNotConnect,
            IOException => ReadFailed,
            _ => Unexpected
        };

    private static string FormatLine(string codec, string container, int? bitrate, int? sampleRate)
    {
        var parts = new List<string> { container == StationSources.HlsContainer ? "HLS" : codec.ToUpperInvariant() };
        if (bitrate is { } kbps)
        {
            parts.Add(kbps.ToString(CultureInfo.InvariantCulture) + " kbps");
        }

        if (sampleRate is { } hz)
        {
            parts.Add((hz / (double)HertzPerKilohertz).ToString("0.###", CultureInfo.InvariantCulture) + " kHz");
        }

        return "Audio: " + string.Join(" · ", parts);
    }

    private static void ApplyAudio(StationProbeResult result, Uri url, string? contentType, StreamAnswer answer)
    {
        result.Ok = true;
        result.StreamUrl = url.AbsoluteUri;
        if (IsHls(url, contentType))
        {
            result.Codec = Aac;
            result.Container = StationSources.HlsContainer;
        }
        else
        {
            var codec = Is(contentType, AacTypes) ? Aac : Mp3;
            result.Codec = codec;
            result.Container = codec;
        }

        result.BitrateKbps = LeadingNumber(answer.IcyBitrate, MinBitrateKbps, MaxBitrateKbps);
        result.SampleRate = LeadingNumber(answer.IcySampleRate, MinSampleRate, MaxSampleRate);
        result.Name = Clean(answer.IcyName);

        // Card L8: what the editor fills into an empty genre, notes and website field.
        result.Genre = StationText.Clean(answer.IcyGenre, StationDefinition.MaxGenreLength);
        result.Notes = StationText.Clean(answer.IcyDescription, StationDefinition.MaxNotesLength);
        // One character over the limit, so a longer header is refused by the website rule instead of cut into another link.
        result.Website = StationUrls.WebsiteFromIcyUrl(StationText.Clean(answer.IcyUrl, StationUrls.MaxWebsiteLength + 1));
    }

    private static bool IsAudio(Uri url, string? contentType)
        => IsHls(url, contentType) || Is(contentType, Mp3Types) || Is(contentType, AacTypes);

    /// <summary>
    /// Inserts the format line (and the HLS warning) at <paramref name="at"/>, after a SUB/WAVE feed may have
    /// replaced the format the headers gave, so the lines describe what is saved.
    /// </summary>
    private static void InsertFormatLines(StationProbeResult result, int at)
    {
        var lines = new List<ProbeLine> { new(ProbeLine.Ok, FormatLine(result.Codec!, result.Container!, result.BitrateKbps, result.SampleRate)) };
        if (result.Container == StationSources.HlsContainer)
        {
            lines.Add(new ProbeLine(ProbeLine.Warning, HlsWarning));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            result.Lines.Insert(at + i, lines[i]);
        }
    }

    /// <summary>
    /// A stored SUB/WAVE base URL that passes the base URL rule, or null.
    /// </summary>
    private static Uri? KnownBase(string? knownBaseUrl)
        => StationUrls.TryParseBaseUrl(knownBaseUrl, out var parsed) ? parsed : null;

    /// <summary>
    /// The places a SUB/WAVE station could answer for an address: the station's stored base URL when the address lies
    /// under it, its origin, the directory the address sits in, and, for an address that is not itself a stream, the
    /// address as a base URL. Each passes the base URL rule.
    /// </summary>
    private static List<Uri> Candidates(Uri url, Uri? knownBase, bool includeSelf)
    {
        var candidates = new List<Uri>();
        void Add(string value)
        {
            if (StationUrls.TryParseBaseUrl(value, out var parsed) && parsed is not null && !candidates.Contains(parsed))
            {
                candidates.Add(parsed);
            }
        }

        if (knownBase is not null
            && Uri.Compare(url, knownBase, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
            && url.AbsolutePath.StartsWith(knownBase.AbsolutePath, StringComparison.Ordinal))
        {
            Add(knownBase.AbsoluteUri);
        }

        Add(url.GetLeftPart(UriPartial.Authority));
        Add(new Uri(url, "./").GetLeftPart(UriPartial.Path));
        if (includeSelf)
        {
            Add(url.GetLeftPart(UriPartial.Path));
        }

        return candidates;
    }

    /// <summary>
    /// The stream path of <paramref name="stream"/> under <paramref name="baseUrl"/>, checked the card 14 way: building
    /// it back through <see cref="StationUrls.TryGetServerStreamUrl(StationDefinition, out Uri)"/> must give the same address.
    /// </summary>
    private static string? StreamPathUnder(Uri baseUrl, Uri stream)
    {
        if (!stream.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return null;
        }

        var path = "/" + stream.AbsolutePath[baseUrl.AbsolutePath.Length..] + stream.Query;
        var station = new StationDefinition { BaseUrl = BaseText(baseUrl), StreamPath = path };
        return StationUrls.TryGetServerStreamUrl(station, out var built) && built is not null
            && string.Equals(built.AbsoluteUri, stream.AbsoluteUri, StringComparison.Ordinal)
            ? path
            : null;
    }

    private static string BaseText(Uri baseUrl) => baseUrl.AbsoluteUri.TrimEnd('/');

    private async Task CheckAsync(Uri url, Uri? knownBase, StationProbeResult result, CancellationToken token, CancellationToken caller)
    {
        var answer = await _client.ProbeStreamAsync(url, type => IsPlaylist(url, type), token).ConfigureAwait(false);
        if (Redirected(answer, result) || !Answered(answer, result))
        {
            return;
        }

        if (IsPlaylist(url, answer.ContentType))
        {
            var entry = Entry(answer, result);
            if (entry is null)
            {
                return;
            }

            result.Lines.Add(new ProbeLine(ProbeLine.Ok, "Playlist read → " + entry.AbsoluteUri));

            // Checked once, never resolved further: a playlist's body is read only for the address the admin pasted.
            var second = await _client.ProbeStreamAsync(entry, _ => false, token).ConfigureAwait(false);
            if (Redirected(second, result) || !Answered(second, result))
            {
                return;
            }

            if (IsPlaylist(entry, second.ContentType))
            {
                result.Lines.Add(new ProbeLine(ProbeLine.Error, NestedPlaylist));
                return;
            }

            url = entry;
            answer = second;
        }

        if (IsAudio(url, answer.ContentType))
        {
            ApplyAudio(result, url, answer.ContentType, answer);
            var at = result.Lines.Count;
            await DetectAsync(url, result, Candidates(url, knownBase, includeSelf: false), token, caller).ConfigureAwait(false);
            InsertFormatLines(result, at);
            return;
        }

        // Not a stream: perhaps the SUB/WAVE base URL (its web player) was pasted. Its feed names the stream mount.
        if (!await DetectAsync(url, result, Candidates(url, knownBase, includeSelf: true), token, caller).ConfigureAwait(false))
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, token.IsCancellationRequested ? TimedOut : NotAudio));
        }
    }

    private bool Redirected(StreamAnswer answer, StationProbeResult result)
    {
        if (!answer.IsRedirect)
        {
            return false;
        }

        // Card 14: suggested only when the target passes the rule the admin's own address had to pass.
        if (answer.Location is { } location && Refusal(location.AbsoluteUri, out var target) is null && target is not null)
        {
            result.Redirect = target.AbsoluteUri;
            result.Lines.Add(new ProbeLine(ProbeLine.Redirect, "The address redirected to " + target.AbsoluteUri + "; use that address"));
        }
        else
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, RedirectRefused));
        }

        return true;
    }

    private bool Answered(StreamAnswer answer, StationProbeResult result)
    {
        if (answer.IsSuccess)
        {
            return true;
        }

        result.Lines.Add(new ProbeLine(ProbeLine.Error, string.Create(CultureInfo.InvariantCulture, $"not reachable: HTTP {answer.Status}")));
        return false;
    }

    private Uri? Entry(StreamAnswer answer, StationProbeResult result)
    {
        if (answer.BodyTooLarge || answer.Body is null)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, PlaylistTooLarge));
            return null;
        }

        if (FirstEntry(answer.Body) is not { } raw)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, PlaylistEmpty));
            return null;
        }

        if (Refusal(raw, out var entry) is { } refusal)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, PlaylistEntryRefused + refusal));
            return null;
        }

        return entry;
    }

    /// <summary>
    /// Looks for a SUB/WAVE station at the candidates, in order. The first whose <c>/api/state</c> answers with a
    /// <c>station</c> block wins: the result becomes a SUB/WAVE station with that base URL, the stream path under it, and
    /// the name, tagline and format from <c>/api/now-playing</c>. For an address that was not a stream, the feed's mount
    /// is checked once (headers only) as the stream. Any failure at a candidate means "not here".
    /// </summary>
    /// <returns>True when a SUB/WAVE station was found and the result is complete.</returns>
    private async Task<bool> DetectAsync(Uri url, StationProbeResult result, List<Uri> candidates, CancellationToken token, CancellationToken caller)
    {
        foreach (var baseUrl in candidates)
        {
            StationState? state;
            try
            {
                state = await _client.GetStateAsync(baseUrl, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!caller.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or InvalidOperationException or IOException)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (state?.Station is null)
            {
                continue;
            }

            NowPlayingFeed? feed = null;
            try
            {
                feed = await _client.GetNowPlayingAsync(baseUrl, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!caller.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or InvalidOperationException or IOException)
            {
                // The name from /api/state is enough; the format stays what the stream's headers said.
            }

            return result.Ok
                ? AdoptStream(url, baseUrl, state, feed, result)
                : await AdoptMountAsync(baseUrl, state, feed, result, token).ConfigureAwait(false);
        }

        if (result.Ok)
        {
            result.Kind = KindStream;
            result.Lines.Add(new ProbeLine(ProbeLine.Ok, token.IsCancellationRequested ? DetectionTimedOut : PlainStream));
        }

        return false;
    }

    private static bool AdoptStream(Uri stream, Uri baseUrl, StationState state, NowPlayingFeed? feed, StationProbeResult result)
    {
        if (StreamPathUnder(baseUrl, stream) is not { } path)
        {
            result.Kind = KindStream;
            result.Lines.Add(new ProbeLine(ProbeLine.Ok, SubWaveOtherServer));
            return false;
        }

        result.Kind = KindSubWave;
        result.BaseUrl = BaseText(baseUrl);
        result.StreamPath = path;
        ApplySubWave(state, feed, result);
        return true;
    }

    private async Task<bool> AdoptMountAsync(Uri baseUrl, StationState state, NowPlayingFeed? feed, StationProbeResult result, CancellationToken token)
    {
        var station = new StationDefinition
        {
            BaseUrl = BaseText(baseUrl),
            StreamPath = string.IsNullOrWhiteSpace(feed?.Stream?.Mount) ? StationDefinition.DefaultStreamPath : feed.Stream.Mount.Trim()
        };
        if (!StationUrls.TryGetServerStreamUrl(station, out var stream) || stream is null || Refusal(stream.AbsoluteUri, out _) is not null)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, SubWaveNoStream));
            return true;
        }

        var answer = await _client.ProbeStreamAsync(stream, _ => false, token).ConfigureAwait(false);
        if (answer.IsRedirect)
        {
            // The station's own stream moved: a hop the station chose, not the admin's address. Named, never offered as
            // "use that address" (the page has no Use it for an error line).
            var target = answer.Location is { } location && Refusal(location.AbsoluteUri, out var accepted) is null && accepted is not null
                ? "redirected to " + accepted.AbsoluteUri + "; Jellywave does not follow it: fix the station's stream settings, or paste that address"
                : "redirected to an address the plugin does not accept";
            result.Lines.Add(new ProbeLine(ProbeLine.Error, MountRedirectPrefix + stream.AbsoluteUri + " " + target));
            return true;
        }

        if (!Answered(answer, result))
        {
            return true;
        }

        if (!IsAudio(stream, answer.ContentType))
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Error, SubWaveNoStream));
            return true;
        }

        ApplyAudio(result, stream, answer.ContentType, answer);
        result.Kind = KindSubWave;
        result.BaseUrl = station.BaseUrl;
        result.StreamPath = StreamPathUnder(baseUrl, stream) ?? station.StreamPath;
        var at = result.Lines.Count;
        ApplySubWave(state, feed, result);
        InsertFormatLines(result, at);
        return true;
    }

    private static void ApplySubWave(StationState state, NowPlayingFeed? feed, StationProbeResult result)
    {
        result.Name = Clean(state.Station?.Name) ?? Clean(feed?.Dj?.Station) ?? result.Name;
        result.Tagline = Clean(feed?.Dj?.Tagline);
        if (feed?.Stream is { } info && result.Container != StationSources.HlsContainer)
        {
            var format = info.Format?.Trim().ToLowerInvariant();
            if (format is Mp3 or Aac)
            {
                result.Codec = format;
                result.Container = format;
            }

            result.BitrateKbps = info.Bitrate is { } kbps && kbps >= MinBitrateKbps && kbps <= MaxBitrateKbps ? kbps : result.BitrateKbps;
            result.SampleRate = info.SampleRate is { } hz && hz >= MinSampleRate && hz <= MaxSampleRate ? hz : result.SampleRate;
            result.Channels = info.Channels is { } channels && channels >= MinChannels && channels <= MaxChannels ? channels : null;
        }

        result.Lines.Add(new ProbeLine(ProbeLine.Ok, "SUB/WAVE detected at " + result.BaseUrl + ": now playing, covers, likes and requests on"));
        if (state.Privacy?.ListenerAuth == true)
        {
            result.Lines.Add(new ProbeLine(ProbeLine.Warning, ListenerAuthWarning));
        }
    }
}
