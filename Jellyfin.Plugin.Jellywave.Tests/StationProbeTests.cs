using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L3: the add-station probe (PLAN diagram 10). A fake station answers by URL; every audio answer's body throws when
/// read, so a probe that reads (or buffers) an audio body fails.
/// </summary>
public class StationProbeTests
{
    private const string Stream = "http://radio.example.org:7700/stream.mp3";
    private const string StateJson = "{\"station\":{\"name\":\"  IHate\\tRadio \"},\"privacy\":{\"listenerAuth\":false}}";
    private const string FeedJson = "{\"stream\":{\"mount\":\"/stream.mp3\",\"format\":\"mp3\",\"bitrate\":192,\"sampleRate\":48000,\"channels\":2},\"dj\":{\"station\":\"Feed name\",\"tagline\":\" late-night\\nstatic \"}}";

    /// <summary>Content whose body cannot be read: reading or buffering it throws.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        public ThrowingContent(string mediaType) => Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        public bool Touched { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Touched = true;
            throw new InvalidOperationException("an audio body was read");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Touched = true;
            throw new InvalidOperationException("an audio body was read");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body of a given size, without a declared length, that counts what was read from it.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly long _length;
        private long _position;

        public CountingStream(string prefix, long length)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            _length = length;
        }

        public long Served => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(Math.Min(count, 4096), _length - _position);
            for (var i = 0; i < n; i++)
            {
                var at = _position + i;
                buffer[offset + i] = at < _prefix.Length ? _prefix[at] : (byte)'#';
            }

            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeStation : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Seen { get; } = new();

        public List<ThrowingContent> AudioBodies { get; } = new();

        public Func<HttpRequestMessage, Exception?>? Fail { get; set; }

        public void Audio(string url, string type, params (string Name, string Value)[] headers)
            => Routes[url] = () =>
            {
                var content = new ThrowingContent(type);
                AudioBodies.Add(content);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            };

        public void Text(string url, string type, string body, HttpStatusCode status = HttpStatusCode.OK)
            => Routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };

        public void Redirect(string url, string location)
            => Routes[url] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("{\"station\":{\"name\":\"x\"}}") };
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            };

        public void SubWave(string baseUrl, string state = StateJson, string feed = FeedJson)
        {
            Text(baseUrl + "/api/state", "application/json", state);
            Text(baseUrl + "/api/now-playing", "application/json", feed);
        }

        public int Count(string url) => Seen.Count(r => r.RequestUri!.AbsoluteUri == url);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            if (Fail?.Invoke(request) is { } failure)
            {
                if (failure is OperationCanceledException)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                throw failure;
            }

            return Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not here") };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static Task<StationProbeResult> Probe(FakeStation station, string url, TimeSpan? budget = null, CancellationToken token = default, string? knownBase = null, TimeSpan? requestTimeout = null)
        => new StationProbe(new StationClient(new StubFactory(station), requestTimeout ?? TimeSpan.FromSeconds(8)), NullLogger.Instance, budget ?? TimeSpan.FromSeconds(30))
            .ProbeAsync(url, knownBase, token);

    private static string[] Texts(StationProbeResult result) => result.Lines.Select(l => l.Level + ": " + l.Text).ToArray();

    [Fact]
    public async Task An_mp3_stream_is_checked_from_its_headers_alone()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-br", "128,128"), ("icy-sr", "44100"), ("icy-name", "  Radio\t\u0001 Paradise  "));

        var result = await Probe(station, Stream);

        Assert.True(result.Ok);
        Assert.Equal("Stream", result.Kind);
        Assert.Equal(Stream, result.StreamUrl);
        Assert.Equal(("mp3", "mp3", 128, 44100), (result.Codec, result.Container, result.BitrateKbps, result.SampleRate));
        Assert.Equal("Radio Paradise", result.Name);
        Assert.Null(result.BaseUrl);
        Assert.Contains("ok: Audio: MP3 · 128 kbps · 44.1 kHz", Texts(result));
        Assert.Contains("ok: " + StationProbe.PlainStream, Texts(result));
        Assert.DoesNotContain(result.Lines, l => l.Level == ProbeLine.Warning);

        // Card L11: an ordinary stream reads as one; SUB/WAVE is named only when one is found.
        Assert.Equal(new[] { "ok: Audio: MP3 · 128 kbps · 44.1 kHz", "ok: Radio stream." }, Texts(result));
        Assert.DoesNotContain(result.Lines, l => l.Text.Contains("SUB/WAVE", StringComparison.Ordinal));

        // Headers only: the request asked for the icy headers, and the endless body was never touched.
        var first = station.Seen[0];
        Assert.Equal(HttpMethod.Get, first.Method);
        Assert.Equal(new[] { "1" }, first.Headers.GetValues("Icy-MetaData"));
        Assert.False(Assert.Single(station.AudioBodies).Touched);

        // SUB/WAVE looked for at the origin only (the stream sits at the root).
        Assert.Equal(new[] { Stream, "http://radio.example.org:7700/api/state" }, station.Seen.Select(r => r.RequestUri!.AbsoluteUri));
    }

    [Theory]
    [InlineData("audio/aac")]
    [InlineData("audio/aacp")]
    [InlineData("AUDIO/AACP")]
    public async Task An_aac_stream_is_added_without_a_warning(string type)
    {
        // Card L10: every station is relayed, and Jellyfin's ffmpeg decodes AAC for every client, so there is nothing to warn.
        var station = new FakeStation();
        station.Audio(Stream, type);

        var result = await Probe(station, Stream);

        Assert.True(result.Ok);
        Assert.Equal(("aac", "aac"), (result.Codec, result.Container));
        Assert.DoesNotContain(result.Lines, l => l.Level == ProbeLine.Warning);
        Assert.DoesNotContain(Texts(result), t => t.Contains("Direct", StringComparison.Ordinal) || t.Contains("Relay", StringComparison.Ordinal));
        Assert.False(station.AudioBodies[0].Touched);
    }

    [Fact]
    public async Task Genre_notes_and_website_come_from_the_icy_headers_cleaned()
    {
        // Card L8: what the editor fills into empty fields; cleaned like icy-name, the website through the website rule.
        var station = new FakeStation();
        station.Audio(
            Stream,
            "audio/mpeg",
            ("icy-genre", "  Eclectic\t\u0001 Seattle "),
            ("icy-description", "Listener-supported,\t<b>90.3</b> FM. "),
            ("icy-url", "www.station.example"));

        var result = await Probe(station, Stream);

        Assert.Equal("Eclectic Seattle", result.Genre);
        Assert.Equal("Listener-supported, <b>90.3</b> FM.", result.Notes);
        Assert.Equal("http://www.station.example/", result.Website);
        Assert.Equal(1, station.Count(Stream));   // the website is never requested
        Assert.DoesNotContain(station.Seen, r => r.RequestUri!.Host.Contains("station.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Long_icy_genre_and_description_are_cut_to_the_limits()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-genre", new string('g', 100)), ("icy-description", new string('d', 400)), ("icy-url", "https://station.example/" + new string('u', 3000)));

        var result = await Probe(station, Stream);

        Assert.Equal(60, result.Genre!.Length);
        Assert.EndsWith("…", result.Genre, StringComparison.Ordinal);
        Assert.Equal(280, result.Notes!.Length);
        Assert.Null(result.Website);   // too long: refused, never cut into another link
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://station.example/")]
    [InlineData("http://user:pw@station.example/")]
    public async Task An_icy_url_the_website_rule_refuses_is_dropped(string icyUrl)
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-url", icyUrl), ("icy-name", "Radio"));

        var result = await Probe(station, Stream);

        Assert.True(result.Ok);
        Assert.Equal("Radio", result.Name);
        Assert.Null(result.Website);
    }

    [Fact]
    public async Task Without_the_icy_headers_genre_notes_and_website_are_null()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-genre", "  "));

        var result = await Probe(station, Stream);

        Assert.Equal(((string?)null, (string?)null, (string?)null), (result.Genre, result.Notes, result.Website));
    }

    [Theory]
    [InlineData("https://hls.example.org/fip/master.m3u8?id=x", "application/vnd.apple.mpegurl")]
    [InlineData("https://hls.example.org/fip/master.M3U8", "audio/x-mpegurl")]   // a .m3u8 path is HLS whatever the type says
    [InlineData("https://hls.example.org/fip/live", "application/x-mpegURL")]
    public async Task Hls_is_a_stream_for_relay_and_its_playlist_is_not_read(string url, string type)
    {
        var station = new FakeStation();
        station.Audio(url, type);

        var result = await Probe(station, url);

        Assert.True(result.Ok);
        Assert.Equal(("aac", "hls"), (result.Codec, result.Container));
        Assert.Contains("ok: Audio: HLS", Texts(result));
        Assert.Contains("warn: " + StationProbe.HlsWarning, Texts(result));
        Assert.DoesNotContain(Texts(result), t => t.Contains("Relay", StringComparison.OrdinalIgnoreCase) || t.Contains("Direct", StringComparison.OrdinalIgnoreCase));   // L10
        Assert.False(station.AudioBodies[0].Touched);
        Assert.Equal(1, station.Count(url));
    }

    [Fact]
    public async Task A_pls_playlist_gives_its_first_absolute_entry_which_is_checked_once()
    {
        var station = new FakeStation();
        const string Pls = "﻿[playlist]\r\n\r\nNumberOfEntries=3\r\nFile1=relative/stream.mp3\r\nTitle1=http://title.example.org/not-a-stream\r\n; comment\r\nFile2=http://s.example.org:8000/live\r\nFile3=http://third.example.org/x.mp3\r\nVersion=2\r\n";
        station.Text("https://radio.example.org/listen.PLS", "text/plain", Pls);
        station.Audio("http://s.example.org:8000/live", "audio/mpeg", ("icy-br", "192"));

        var result = await Probe(station, "https://radio.example.org/listen.PLS");

        Assert.True(result.Ok);
        Assert.Equal("http://s.example.org:8000/live", result.StreamUrl);
        Assert.Equal(192, result.BitrateKbps);
        Assert.Equal("ok: Playlist read → http://s.example.org:8000/live", Texts(result)[0]);
        Assert.Equal(1, station.Count("http://s.example.org:8000/live"));
        Assert.Equal(0, station.Count("http://third.example.org/x.mp3"));
        Assert.Equal(0, station.Count("http://title.example.org/not-a-stream"));
    }

    [Fact]
    public async Task An_m3u_by_content_type_skips_comments_blank_and_relative_lines()
    {
        var station = new FakeStation();
        const string M3u = "#EXTM3U\n\n#EXTINF:-1,http://comment.example.org/x.mp3\nstream.mp3\n/abs/path.mp3\n   \nhttps://cdn.example.org/live.aac\nhttp://second.example.org/x.mp3\n";
        station.Text("http://radio.example.org/listen", "audio/x-mpegurl", M3u);
        station.Audio("https://cdn.example.org/live.aac", "audio/aac");

        var result = await Probe(station, "http://radio.example.org/listen");

        Assert.True(result.Ok);
        Assert.Equal("https://cdn.example.org/live.aac", result.StreamUrl);
        Assert.Equal("aac", result.Codec);
        Assert.Equal(0, station.Count("http://comment.example.org/x.mp3"));
    }

    [Theory]
    [InlineData("audio/x-scpls")]
    [InlineData("audio/mpegurl")]
    [InlineData("text/plain")]
    public async Task A_playlist_pointing_at_a_playlist_is_checked_once_then_refused(string secondType)
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org/a.m3u", "audio/x-mpegurl", "http://radio.example.org/b.m3u\n");
        station.Audio("http://radio.example.org/b.m3u", secondType);

        var result = await Probe(station, "http://radio.example.org/a.m3u");

        Assert.False(result.Ok);
        Assert.Null(result.Kind);
        Assert.Equal("error: " + StationProbe.NestedPlaylist, Texts(result)[^1]);
        Assert.Equal(1, station.Count("http://radio.example.org/b.m3u"));
        Assert.False(station.AudioBodies[0].Touched);   // the second playlist's body is never read
        Assert.Equal(2, station.Seen.Count);
    }

    [Theory]
    [InlineData("[playlist]\nNumberOfEntries=0\n")]
    [InlineData("#EXTM3U\n# nothing\nrelative.mp3\nftp://files.example.org/x.mp3\n")]
    public async Task A_playlist_without_an_http_entry_says_so(string body)
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org/x.pls", "audio/x-scpls", body);

        var result = await Probe(station, "http://radio.example.org/x.pls");

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: " + StationProbe.PlaylistEmpty }, Texts(result));
    }

    [Fact]
    public async Task A_playlist_entry_with_credentials_is_refused_and_not_shown()
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org/x.m3u", "audio/x-mpegurl", "http://user:secret@s.example.org/live\n");

        var result = await Probe(station, "http://radio.example.org/x.m3u");

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: " + StationProbe.PlaylistEntryRefused + StationUrls.RefusedCredentials }, Texts(result));
        Assert.Single(station.Seen);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_playlist_over_256_kb_is_not_read_further()
    {
        var station = new FakeStation();
        var body = new CountingStream("http://s.example.org/live.mp3\n", (10 * StationClient.MaxPlaylistBytes) + 7);
        station.Routes["http://radio.example.org/big.pls"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("audio/x-scpls") } }
        };

        var result = await Probe(station, "http://radio.example.org/big.pls");

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: " + StationProbe.PlaylistTooLarge }, Texts(result));
        Assert.Equal(StationClient.MaxPlaylistBytes + 1, body.Served);
        Assert.Single(station.Seen);
    }

    [Fact]
    public async Task A_playlist_of_exactly_256_kb_is_read()
    {
        var station = new FakeStation();
        var body = new CountingStream("File1=http://s.example.org/live.mp3\n", StationClient.MaxPlaylistBytes);
        station.Routes["http://radio.example.org/full.pls"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("audio/x-scpls") } }
        };
        station.Audio("http://s.example.org/live.mp3", "audio/mpeg");

        var result = await Probe(station, "http://radio.example.org/full.pls");

        Assert.True(result.Ok);
        Assert.Equal("http://s.example.org/live.mp3", result.StreamUrl);
    }

    [Fact]
    public async Task A_playlist_that_declares_more_than_256_kb_is_not_read_at_all()
    {
        var station = new FakeStation();
        var content = new ThrowingContent("audio/x-scpls");
        content.Headers.ContentLength = StationClient.MaxPlaylistBytes + 1;
        station.Routes["http://radio.example.org/huge.pls"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        var result = await Probe(station, "http://radio.example.org/huge.pls");

        Assert.Equal(new[] { "error: " + StationProbe.PlaylistTooLarge }, Texts(result));
        Assert.False(content.Touched);
    }

    [Fact]
    public async Task A_redirect_to_an_acceptable_address_is_suggested_and_not_followed()
    {
        var station = new FakeStation();
        station.Redirect(Stream, "https://other.example.org/live.mp3?token=a");
        station.Audio("https://other.example.org/live.mp3?token=a", "audio/mpeg");

        var result = await Probe(station, Stream);

        Assert.False(result.Ok);
        Assert.Null(result.Kind);
        Assert.Equal("https://other.example.org/live.mp3?token=a", result.Redirect);
        Assert.Equal(new[] { "redirect: The address redirected to https://other.example.org/live.mp3?token=a; use that address" }, Texts(result));
        Assert.Single(station.Seen);
    }

    [Fact]
    public async Task A_relative_redirect_is_resolved_against_the_address()
    {
        var station = new FakeStation();
        station.Redirect(Stream, "/live/stream.mp3");

        var result = await Probe(station, Stream);

        Assert.Equal("http://radio.example.org:7700/live/stream.mp3", result.Redirect);
    }

    [Theory]
    [InlineData("http://user:secret@evil.example.org/live.mp3")]
    [InlineData("ftp://evil.example.org/live.mp3")]
    [InlineData("http://evil.example.org/live.mp3#secret")]
    [InlineData("http://evil.example.org/live.mpd")]
    public async Task A_redirect_to_an_unacceptable_address_shows_none_of_it(string location)
    {
        var station = new FakeStation();
        station.Redirect(Stream, location);

        var result = await Probe(station, Stream);

        Assert.False(result.Ok);
        Assert.Null(result.Redirect);
        Assert.Equal(new[] { "error: " + StationProbe.RedirectRefused }, Texts(result));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("evil", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Single(station.Seen);
    }

    [Fact]
    public async Task A_redirect_inside_a_playlist_is_suggested_for_the_entry()
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org/x.pls", "audio/x-scpls", "File1=http://s.example.org/live\n");
        station.Redirect("http://s.example.org/live", "https://s.example.org/live");

        var result = await Probe(station, "http://radio.example.org/x.pls");

        Assert.Equal("https://s.example.org/live", result.Redirect);
    }

    [Fact]
    public async Task A_sub_wave_station_at_the_origin_is_detected_and_its_feed_fills_the_format()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-br", "128"), ("icy-name", "Icecast name"));
        station.SubWave("http://radio.example.org:7700");

        var result = await Probe(station, Stream);

        Assert.True(result.Ok);
        Assert.Equal("SubWave", result.Kind);
        Assert.Equal("http://radio.example.org:7700", result.BaseUrl);
        Assert.Equal("/stream.mp3", result.StreamPath);
        Assert.Equal("IHate Radio", result.Name);
        Assert.Equal("late-night static", result.Tagline);
        Assert.Equal((192, 48000, 2), (result.BitrateKbps, result.SampleRate, result.Channels));
        Assert.Contains("ok: SUB/WAVE detected at http://radio.example.org:7700: now playing, covers, likes and requests on", Texts(result));
        Assert.DoesNotContain(result.Lines, l => l.Text == StationProbe.PlainStream);
    }

    [Fact]
    public async Task A_sub_wave_station_under_a_path_prefix_is_detected_there()
    {
        var station = new FakeStation();
        const string Prefixed = "https://host.example.org/radio/stream.mp3?x=1";
        station.Audio(Prefixed, "audio/mpeg");
        station.SubWave("https://host.example.org/radio");

        var result = await Probe(station, Prefixed);

        Assert.Equal("SubWave", result.Kind);
        Assert.Equal("https://host.example.org/radio", result.BaseUrl);
        Assert.Equal("/stream.mp3?x=1", result.StreamPath);
        Assert.Equal(1, station.Count("https://host.example.org/api/state"));
        Assert.Equal(1, station.Count("https://host.example.org/radio/api/state"));
    }

    [Fact]
    public async Task A_stream_whose_path_would_rebuild_to_another_address_stays_a_plain_stream()
    {
        // "/radio//live.mp3" under "/radio/" gives the stream path "//live.mp3", which the card 14 rule trims to
        // "/radio/live.mp3": another address. The probe keeps the address it checked instead of storing that.
        var station = new FakeStation();
        const string Doubled = "https://host.example.org/radio//live.mp3";
        station.Audio(Doubled, "audio/mpeg");
        station.SubWave("https://host.example.org/radio");

        var result = await Probe(station, Doubled);

        Assert.True(result.Ok);
        Assert.Equal("Stream", result.Kind);
        Assert.Null(result.BaseUrl);
        Assert.Equal(Doubled, result.StreamUrl);
        Assert.Contains("ok: " + StationProbe.SubWaveOtherServer, Texts(result));
    }

    [Fact]
    public async Task The_sub_wave_base_url_itself_finds_its_stream_from_the_feed()
    {
        // An upgrading admin pastes the old base URL: its web player is not audio, but the feed names the mount.
        var station = new FakeStation();
        station.Text("http://radio.example.org:7700/", "text/html", "<html>player</html>");
        station.SubWave("http://radio.example.org:7700", feed: FeedJson.Replace("/stream.mp3", "/live/hi.mp3", StringComparison.Ordinal));
        station.Audio("http://radio.example.org:7700/live/hi.mp3", "audio/mpeg");

        var result = await Probe(station, "http://radio.example.org:7700");

        Assert.True(result.Ok);
        Assert.Equal("SubWave", result.Kind);
        Assert.Equal("http://radio.example.org:7700", result.BaseUrl);
        Assert.Equal("/live/hi.mp3", result.StreamPath);
        Assert.Equal("http://radio.example.org:7700/live/hi.mp3", result.StreamUrl);
        Assert.False(station.AudioBodies[0].Touched);
        Assert.Equal("ok: Audio: MP3 · 192 kbps · 48 kHz", Texts(result)[0]);   // the feed's format, not the bare headers
    }

    [Fact]
    public async Task A_sub_wave_base_url_under_a_prefix_is_tried_as_itself()
    {
        // "https://host.example.org/radio" sits in the root directory, so only the address itself names the prefix.
        var station = new FakeStation();
        station.Text("https://host.example.org/radio", "text/html", "<html>player</html>");
        station.SubWave("https://host.example.org/radio");
        station.Audio("https://host.example.org/radio/stream.mp3", "audio/mpeg");

        var result = await Probe(station, "https://host.example.org/radio");

        Assert.Equal("SubWave", result.Kind);
        Assert.Equal(("https://host.example.org/radio", "/stream.mp3"), (result.BaseUrl, result.StreamPath));
        Assert.Equal(1, station.Count("https://host.example.org/api/state"));
    }

    [Fact]
    public async Task A_feed_mount_that_leaves_the_base_is_refused()
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org:7700/", "text/html", "<html>player</html>");
        station.SubWave("http://radio.example.org:7700", feed: "{\"stream\":{\"mount\":\"http://elsewhere.example.org/x.mp3\"}}");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.False(result.Ok);
        Assert.Contains("error: " + StationProbe.SubWaveNoStream, Texts(result));
        Assert.Equal(0, station.Count("http://elsewhere.example.org/x.mp3"));
    }

    [Fact]
    public async Task Listener_auth_is_a_warning()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg");
        station.SubWave("http://radio.example.org:7700", state: "{\"station\":{\"name\":\"Private\"},\"privacy\":{\"listenerAuth\":true}}");

        var result = await Probe(station, Stream);

        Assert.Contains("warn: " + StationProbe.ListenerAuthWarning, Texts(result));
    }

    [Theory]
    [InlineData("{\"name\":\"not sub/wave\"}")]
    [InlineData("[1,2]")]
    [InlineData("<html></html>")]
    public async Task Json_that_is_not_sub_wave_shaped_leaves_a_plain_stream(string state)
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg");
        station.Text("http://radio.example.org:7700/api/state", "application/json", state);

        var result = await Probe(station, Stream);

        Assert.Equal("Stream", result.Kind);
        Assert.Null(result.BaseUrl);
    }

    [Fact]
    public async Task Something_that_is_not_audio_and_not_sub_wave_is_refused()
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org/page", "text/html", "<html></html>");

        var result = await Probe(station, "http://radio.example.org/page");

        Assert.False(result.Ok);
        Assert.Null(result.Kind);
        Assert.Equal(new[] { "error: " + StationProbe.NotAudio }, Texts(result));
    }

    [Fact]
    public async Task An_error_status_is_reported_by_number()
    {
        var station = new FakeStation();

        var result = await Probe(station, Stream);

        Assert.Equal(new[] { "error: not reachable: HTTP 404" }, Texts(result));
        Assert.Single(station.Seen);
    }

    [Theory]
    [InlineData("", "no address given")]
    [InlineData("ftp://radio.example.org/x.mp3", StationUrls.RefusedScheme)]
    [InlineData("http://user:secret@radio.example.org/x.mp3", StationUrls.RefusedCredentials)]
    [InlineData("http://radio.example.org/x.mp3#secret", StationUrls.RefusedFragment)]
    [InlineData("http://radio.example.org/x.mp3#", StationUrls.RefusedFragment)]
    [InlineData("not a url", StationUrls.RefusedUnparsable)]
    [InlineData("http://radio.example.org/manifest.MPD", StationProbe.RefusedDash)]
    public async Task A_refused_url_is_never_requested(string url, string reason)
    {
        var station = new FakeStation();

        var result = await Probe(station, url);

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: URL not allowed: " + reason }, Texts(result));
        Assert.Empty(station.Seen);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_is_a_category()
    {
        var station = new FakeStation { Fail = _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()) };

        var result = await Probe(station, Stream, TimeSpan.FromMilliseconds(200));

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: " + StationProbe.TimedOut }, Texts(result));
    }

    [Fact]
    public async Task A_client_timeout_that_throws_at_once_is_a_category_too()
    {
        var station = new FakeStation();
        station.Routes[Stream] = () => throw new TaskCanceledException("timeout", new TimeoutException());

        var result = await Probe(station, Stream);

        Assert.Equal(new[] { "error: " + StationProbe.TimedOut }, Texts(result));
    }

    [Fact]
    public async Task The_sub_wave_check_running_out_of_time_keeps_the_stream()
    {
        var station = new FakeStation { Fail = r => r.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ? new OperationCanceledException() : null };
        station.Audio(Stream, "audio/mpeg");

        var result = await Probe(station, Stream, TimeSpan.FromMilliseconds(300));

        Assert.True(result.Ok);
        Assert.Equal("Stream", result.Kind);
        Assert.Contains("ok: " + StationProbe.DetectionTimedOut, Texts(result));
    }

    [Theory]
    [InlineData("Received an invalid status line: 'ICY 200 OK'.", StationProbe.ShoutcastV1)]
    [InlineData("Received an invalid status line: 'garbage'.", StationProbe.InvalidResponse)]
    public async Task An_invalid_response_is_a_category(string message, string expected)
    {
        var station = new FakeStation { Fail = _ => new HttpRequestException(HttpRequestError.InvalidResponse, message) };

        var result = await Probe(station, Stream);

        Assert.Equal(new[] { "error: " + expected }, Texts(result));
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, StationProbe.CouldNotConnect)]
    [InlineData(HttpRequestError.ProxyTunnelError, StationProbe.CouldNotConnect)]
    [InlineData(HttpRequestError.NameResolutionError, StationProbe.NameNotFound)]
    [InlineData(HttpRequestError.SecureConnectionError, StationProbe.TlsFailed)]
    [InlineData(HttpRequestError.ResponseEnded, StationProbe.ConnectionClosed)]
    public async Task A_connection_failure_is_a_category_without_the_exception_text(HttpRequestError error, string expected)
    {
        var station = new FakeStation { Fail = _ => new HttpRequestException(error, "Connection refused (radio.example.org:7700) secret") };

        var result = await Probe(station, Stream);

        Assert.Equal(new[] { "error: " + expected }, Texts(result));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_category()
    {
        var station = new FakeStation { Fail = _ => new FormatException("secret detail") };

        var result = await Probe(station, Stream);

        Assert.Equal(new[] { "error: " + StationProbe.Unexpected }, Texts(result));
    }

    [Fact]
    public async Task The_caller_cancelling_stops_the_probe()
    {
        using var cts = new CancellationTokenSource();
        var station = new FakeStation
        {
            Fail = _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(station, Stream, token: cts.Token));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("\u0001\u0002", null)]
    [InlineData(" a\r\n\tb ", "a b")]
    public void Station_text_is_one_clean_line(string? value, string? expected)
        => Assert.Equal(expected, StationProbe.Clean(value));

    [Fact]
    public void Station_text_is_cut_at_120()
    {
        var cleaned = StationProbe.Clean(new string('x', 500))!;
        Assert.Equal(NowPlayingState.MaxFieldLength, cleaned.Length);
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
        Assert.Equal(new string('y', 120), StationProbe.Clean(new string('y', 120)));
    }

    [Theory]
    [InlineData("128", 128)]
    [InlineData(" 320,320", 320)]
    [InlineData("128 kbps", 128)]
    [InlineData("7", null)]
    [InlineData("1537", null)]
    [InlineData("99999999999", null)]
    [InlineData("-128", null)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void Icy_numbers_are_bounded(string? value, int? expected)
        => Assert.Equal(expected, StationProbe.LeadingNumber(value, 8, 1536));

    [Fact]
    public void The_probe_route_is_an_admin_post()
    {
        var method = typeof(JellywaveController).GetMethod(nameof(JellywaveController.Probe))!;
        Assert.Equal("Probe", method.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Contains(typeof(JellywaveController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == Policies.RequiresElevation);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(typeof(JellywaveController).GetMethod("ProbeStation"));
    }

    /// <summary>A playlist body that fails or never ends, to test the body read's own budget and its failure.</summary>
    private sealed class StuckStream : Stream
    {
        private readonly Exception? _failure;

        public StuckStream(Exception? failure = null) => _failure = failure;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw _failure ?? new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Func<HttpResponseMessage> PlaylistOf(Stream body)
        => () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("audio/x-mpegurl") } }
        };

    [Fact]
    public async Task A_playlist_body_that_never_ends_times_out_on_its_own_budget()
    {
        var station = new FakeStation();
        station.Routes["http://radio.example.org/slow.m3u"] = PlaylistOf(new StuckStream());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Probe(station, "http://radio.example.org/slow.m3u", budget: TimeSpan.FromSeconds(20), requestTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(new[] { "error: " + StationProbe.TimedOut }, Texts(result));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "took " + watch.Elapsed);
    }

    [Fact]
    public async Task Headers_that_never_come_time_out_on_the_per_call_budget()
    {
        var station = new FakeStation { Fail = _ => new OperationCanceledException() };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Probe(station, Stream, budget: TimeSpan.FromSeconds(20), requestTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(new[] { "error: " + StationProbe.TimedOut }, Texts(result));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "took " + watch.Elapsed);
    }

    [Fact]
    public async Task A_playlist_body_that_breaks_is_a_read_failure()
    {
        var station = new FakeStation();
        station.Routes["http://radio.example.org/broken.m3u"] = PlaylistOf(new StuckStream(new IOException("connection reset secret")));

        var result = await Probe(station, "http://radio.example.org/broken.m3u");

        Assert.Equal(new[] { "error: " + StationProbe.ReadFailed }, Texts(result));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_playlist_with_a_byte_order_mark_parses()
    {
        var station = new FakeStation();
        station.Routes["http://radio.example.org/bom.m3u"] = PlaylistOf(new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("http://s.example.org/live.mp3\r\n")).ToArray()));
        station.Audio("http://s.example.org/live.mp3", "audio/mpeg");

        var result = await Probe(station, "http://radio.example.org/bom.m3u");

        Assert.True(result.Ok);
        Assert.Equal("http://s.example.org/live.mp3", result.StreamUrl);
        Assert.Equal("http://s.example.org/live.mp3", StationProbe.FirstEntry("\uFEFFhttp://s.example.org/live.mp3\n"));

        // The client hands the body over without the mark, whatever the parser does with one.
        station.Routes["http://radio.example.org/bom.m3u"] = PlaylistOf(new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("http://s.example.org/live.mp3\r\n")).ToArray()));
        var answer = await new StationClient(new StubFactory(station)).ProbeStreamAsync(new Uri("http://radio.example.org/bom.m3u"), _ => true, CancellationToken.None);
        Assert.Equal("http://s.example.org/live.mp3\r\n", answer.Body);
    }

    [Theory]
    [InlineData("/api/state")]
    [InlineData("/api/now-playing")]
    public async Task The_caller_cancelling_during_the_sub_wave_check_stops_the_probe(string path)
    {
        using var cts = new CancellationTokenSource();
        var station = new FakeStation
        {
            Fail = r =>
            {
                if (r.RequestUri!.AbsolutePath != path)
                {
                    return null;
                }

                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }
        };
        station.Audio(Stream, "audio/mpeg");
        station.SubWave("http://radio.example.org:7700");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(station, Stream, token: cts.Token));
    }

    [Fact]
    public async Task Something_that_is_not_audio_running_out_of_time_says_timed_out()
    {
        var station = new FakeStation { Fail = r => r.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ? new OperationCanceledException() : null };
        station.Text("http://radio.example.org/page", "text/html", "<html></html>");

        var result = await Probe(station, "http://radio.example.org/page", TimeSpan.FromMilliseconds(300));

        Assert.False(result.Ok);
        Assert.Equal(new[] { "error: " + StationProbe.TimedOut }, Texts(result));
    }

    private static FakeStation PastedBase(string feed)
    {
        var station = new FakeStation();
        station.Text("http://radio.example.org:7700/", "text/html", "<html>player</html>");
        station.SubWave("http://radio.example.org:7700", feed: feed);
        return station;
    }

    [Fact]
    public async Task A_station_stream_that_redirects_is_named_and_not_offered()
    {
        var station = PastedBase(FeedJson);
        station.Redirect("http://radio.example.org:7700/stream.mp3", "https://cdn.example.org/s.mp3");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.False(result.Ok);
        Assert.Null(result.Redirect);
        Assert.Equal(
            "error: " + StationProbe.MountRedirectPrefix + "http://radio.example.org:7700/stream.mp3 redirected to https://cdn.example.org/s.mp3; Jellywave does not follow it: fix the station's stream settings, or paste that address",
            Texts(result).Single());
        Assert.Equal(0, station.Count("https://cdn.example.org/s.mp3"));
    }

    [Fact]
    public async Task A_station_stream_that_redirects_somewhere_refused_shows_none_of_it()
    {
        var station = PastedBase(FeedJson);
        station.Redirect("http://radio.example.org:7700/stream.mp3", "http://user:secret@evil.example.org/s.mp3");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.Equal("error: " + StationProbe.MountRedirectPrefix + "http://radio.example.org:7700/stream.mp3 redirected to an address the plugin does not accept", Texts(result).Single());
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_station_stream_that_is_not_audio_is_refused()
    {
        var station = PastedBase(FeedJson);
        station.Text("http://radio.example.org:7700/stream.mp3", "text/html", "<html>offline</html>");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.False(result.Ok);
        Assert.Contains("error: " + StationProbe.SubWaveNoStream, Texts(result));
    }

    [Theory]
    [InlineData("/stream.mp3#x")]
    [InlineData("/live.MPD")]
    public async Task A_station_mount_that_fails_the_whole_url_rule_is_never_requested(string mount)
    {
        var station = PastedBase(FeedJson.Replace("/stream.mp3", mount, StringComparison.Ordinal));
        station.Audio("http://radio.example.org:7700/stream.mp3", "audio/mpeg");
        station.Audio("http://radio.example.org:7700/live.MPD", "audio/mpeg");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.False(result.Ok);
        Assert.Contains("error: " + StationProbe.SubWaveNoStream, Texts(result));
        Assert.Empty(station.AudioBodies);
    }

    [Fact]
    public async Task A_blank_mount_means_stream_mp3()
    {
        var station = PastedBase(FeedJson.Replace("/stream.mp3", "  ", StringComparison.Ordinal));
        station.Audio("http://radio.example.org:7700/stream.mp3", "audio/mpeg");

        var result = await Probe(station, "http://radio.example.org:7700/");

        Assert.True(result.Ok);
        Assert.Equal("/stream.mp3", result.StreamPath);
    }

    [Fact]
    public async Task A_sub_wave_feed_does_not_turn_hls_into_mp3()
    {
        var station = new FakeStation();
        const string Hls = "http://radio.example.org:7700/live/master.m3u8";
        station.Audio(Hls, "application/vnd.apple.mpegurl");
        station.SubWave("http://radio.example.org:7700");

        var result = await Probe(station, Hls);

        Assert.Equal("SubWave", result.Kind);
        Assert.Equal(("aac", "hls"), (result.Codec, result.Container));
        Assert.Contains("warn: " + StationProbe.HlsWarning, Texts(result));
    }

    [Fact]
    public async Task The_format_lines_follow_the_feed_not_the_headers()
    {
        // Headers say AAC, the station's feed says MP3: what is saved is MP3.
        var station = new FakeStation();
        station.Audio(Stream, "audio/aac", ("icy-br", "64"));
        station.SubWave("http://radio.example.org:7700");

        var result = await Probe(station, Stream);

        Assert.Equal(("mp3", "mp3", 192), (result.Codec, result.Container, result.BitrateKbps));
        Assert.Equal("ok: Audio: MP3 · 192 kbps · 48 kHz", Texts(result)[0]);
    }

    [Theory]
    [InlineData(9, 5000, null, 128)]
    [InlineData(0, 7, null, 128)]
    [InlineData(1, 1536, 1, 1536)]
    [InlineData(8, 8, 8, 8)]
    public async Task Feed_numbers_are_bounded(int channels, int bitrate, int? expectedChannels, int expectedBitrate)
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg", ("icy-br", "128"));
        station.SubWave("http://radio.example.org:7700", feed: "{\"stream\":{\"format\":\"mp3\",\"bitrate\":" + bitrate + ",\"channels\":" + channels + "}}");

        var result = await Probe(station, Stream);

        Assert.Equal((expectedChannels, expectedBitrate), (result.Channels, result.BitrateKbps));
    }

    [Fact]
    public async Task A_stored_base_finds_a_station_whose_stream_sits_deeper_than_its_directory()
    {
        var station = new FakeStation();
        const string Deep = "https://host.example.org/radio/sub/live.mp3";
        station.Audio(Deep, "audio/mpeg");
        station.SubWave("https://host.example.org/radio");

        var without = await Probe(station, Deep);
        var with = await Probe(station, Deep, knownBase: "https://host.example.org/radio/");

        Assert.Equal("Stream", without.Kind);
        Assert.Equal(("SubWave", "https://host.example.org/radio", "/sub/live.mp3"), (with.Kind, with.BaseUrl, with.StreamPath));
    }

    [Theory]
    [InlineData("https://other.example.org/radio")]
    [InlineData("http://host.example.org/radio")]
    [InlineData("https://host.example.org:8443/radio")]
    [InlineData("https://host.example.org/elsewhere")]
    [InlineData("https://user:pw@host.example.org/radio")]
    public async Task A_stored_base_the_address_does_not_lie_under_is_not_asked(string knownBase)
    {
        var station = new FakeStation();
        const string Deep = "https://host.example.org/radio/sub/live.mp3";
        station.Audio(Deep, "audio/mpeg");

        var result = await Probe(station, Deep, knownBase: knownBase);

        Assert.Equal("Stream", result.Kind);
        Assert.Equal(new[] { Deep, "https://host.example.org/api/state", "https://host.example.org/radio/sub/api/state" }, station.Seen.Select(r => r.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public void The_probe_takes_its_address_from_the_body()
    {
        var parameter = typeof(JellywaveController).GetMethod(nameof(JellywaveController.Probe))!.GetParameters()[0];
        Assert.NotNull(parameter.GetCustomAttribute<FromBodyAttribute>());
        Assert.Null(parameter.GetCustomAttribute<FromQueryAttribute>());
    }

    [Fact]
    public async Task The_route_answers_with_the_probe()
    {
        var station = new FakeStation();
        station.Audio(Stream, "audio/mpeg");
        var controller = new JellywaveController(new StationClient(new StubFactory(station)), null!, new NowPlayingState(), new PollerHealth(), null!, null!, null!, new StationTitles(), null!, NullLogger<JellywaveController>.Instance);

        var result = (await controller.Probe(new ProbeRequest { Url = Stream }, CancellationToken.None)).Value!;

        Assert.True(result.Ok);
        Assert.Equal(Stream, result.StreamUrl);

        // The stored base of the station being edited is passed through to the probe.
        const string Deep = "http://radio.example.org:7700/radio/sub/live.mp3";
        station.Audio(Deep, "audio/mpeg");
        station.SubWave("http://radio.example.org:7700/radio");
        var edited = (await controller.Probe(new ProbeRequest { Url = Deep, BaseUrl = "http://radio.example.org:7700/radio" }, CancellationToken.None)).Value!;
        Assert.Equal("SubWave", edited.Kind);
    }
}
