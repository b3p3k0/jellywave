using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L5: a tile with two listed SUB/WAVE stations ("live", the primary, and "two"), a plain stream, a disabled
/// SUB/WAVE station, one with a refused base URL and a later duplicate of "two", every station on its own host. One stub
/// answers every station endpoint and records each request's host and path, so a test can say which station was asked.
/// </summary>
internal sealed class MultiStationHarness
{
    public const string HostLive = "station-live.example";
    public const string HostTwo = "station-two.example";
    public const string HostOff = "station-off.example";
    public const string HostBad = "station-bad.example";
    public const string HostDup = "station-dup.example";
    public const string HostPlain = "cdn-plain.example";

    public MultiStationHarness()
    {
        Client = new StationClient(new StubFactory(Station));
        Schedule = new ScheduleCache(Client);
        Library.ListedItems.Add(LiveItem);
        Library.ListedItems.Add(TwoItem);
        Library.ListedItems.Add(PlainItem);
        LiveItem.SetImagePath(ImageType.Primary, "http://saved.example/live");
        TwoItem.SetImagePath(ImageType.Primary, "http://saved.example/two");
        foreach (var (id, host, song) in new[] { ("live", HostLive, "song-live"), ("two", HostTwo, "song-two") })
        {
            State.Update(
                KeyOf(id, host),
                new NowPlayingFeed
                {
                    NowPlaying = new TrackInfo { Artist = "Artist " + id, Title = "Title " + id, SubsonicId = song },
                    Dj = new DjInfo { Name = "DJ " + id, Avatar = "/persona-avatar/dj_" + id },
                    StreamOnline = true
                },
                DateTime.UtcNow);
        }
    }

    /// <summary>The ids no station route may resolve: unknown, disabled, refused, plain stream, blank and case-changed.</summary>
    public static IEnumerable<object[]> UnlistedIds()
        => new[] { "nope", "off", "bad", "plain", string.Empty, " ", "LIVE", "Two", "two ", "../two" }.Select(id => new object[] { id });

    public PluginConfiguration Config { get; } = new()
    {
        LibraryName = "Live Radio",
        Stations =
        {
            new StationDefinition { Id = "live", Name = "Live One", BaseUrl = "http://" + HostLive + "/radio", SortOrder = 0 },
            new StationDefinition { Id = "two", Name = "Station <b>Two</b>", BaseUrl = "http://" + HostTwo + ":7700", StreamPath = "/hidden-mount.mp3", SortOrder = 1 },
            new StationDefinition { Id = "plain", Name = "Plain", Kind = StationKind.Stream, StreamUrl = "https://" + HostPlain + "/p.mp3?token=secret-query", SortOrder = 2 },
            new StationDefinition { Id = "off", Name = "Off", BaseUrl = "http://" + HostOff, Enabled = false, SortOrder = 3 },
            new StationDefinition { Id = "bad", Name = "Bad", BaseUrl = "http://" + HostBad + "/?x=1", SortOrder = 4 },
            new StationDefinition { Id = "two", Name = "Dup", BaseUrl = "http://" + HostDup, SortOrder = 5 }
        }
    };

    public StubStation Station { get; } = new();

    public StationClient Client { get; }

    public ScheduleCache Schedule { get; }

    public RequestReceipts Receipts { get; } = new();

    /// <summary>The plain streams' sampled titles (card L6); empty until a test records one.</summary>
    public StationTitles Titles { get; } = new();

    public NowPlayingState State { get; } = new();

    public FakeLibrary Library { get; } = FakeLibrary.Create();

    public Audio LiveItem { get; } = new() { Id = Guid.NewGuid(), ExternalId = "live" };

    public Audio TwoItem { get; } = new() { Id = Guid.NewGuid(), ExternalId = "two" };

    public Audio PlainItem { get; } = new() { Id = Guid.NewGuid(), ExternalId = "plain" };

    public Channel Channel { get; } = new() { Id = Guid.NewGuid(), Name = "Live Radio" };

    /// <summary>Which stations someone needs (card L14): the station route filter records the open pages here.</summary>
    public StationDemand Demand { get; } = new(new FakePlayingItems());

    public LiveItemArtwork Artwork { get; } = new(new StationClient(null!), (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance);

    /// <summary>Every request any station saw, as "host METHOD path".</summary>
    public IReadOnlyList<string> Seen => Station.Seen;

    public static StationKey KeyOf(string id, string host)
        => StationKey.Of(id, new Uri("http://" + host + (host == HostTwo ? ":7700/" : host == HostLive ? "/radio/" : "/")));

    /// <summary>A controller as DI builds it; card R2a: a new <see cref="StationCalls"/> (fresh budgets, limiter and caches)
    /// for each, unless the test hands in one to share.</summary>
    public JellywaveWebController Controller(Guid? user = null, StationCalls? calls = null)
        => WebRoute.Controller(
            () => Config,
            State,
            Artwork,
            new LiveItemFinder(Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            DispatchProxy.Create<IImageProcessor, FeedStationSwitchTests.TagProcessor>(),
            Client,
            Schedule,
            Receipts,
            user,
            Channel,
            Titles,
            Demand,
            calls);

    /// <summary>Runs one route as the given user (default: <see cref="WebRoute.Listener"/>), with or without a station id.</summary>
    public Task<IActionResult> RunAsync(string action, string? stationId, Guid? user = null)
    {
        var args = new List<(string Name, object? Value)>();
        switch (action)
        {
            case nameof(JellywaveWebController.Like):
                args.Add(("submission", new LikeSubmission { SongId = "song-x", StationId = stationId }));
                break;
            case nameof(JellywaveWebController.SubmitRequest):
                args.Add(("submission", new RequestSubmission { Text = "something with horns", Name = "A", StationId = stationId }));
                break;
            default:
                args.Add(("songId", "song-x"));
                args.Add(("personaId", "dj_x"));
                if (stationId is not null)
                {
                    args.Add(("stationId", stationId));
                }

                break;
        }

        return WebRoute.RunAsync(Controller(user), action, [.. args]);
    }

    /// <summary>Answers every SUB/WAVE endpoint the routes call; the bodies name the host that answered.</summary>
    public sealed class StubStation : HttpMessageHandler
    {
        public List<string> Seen { get; } = new();

        public HashSet<string> RequestStatusNotFound { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Seen)
            {
                Seen.Add(host + " " + request.Method + " " + path + (body.Length > 0 ? " " + body : string.Empty));
            }

            if (path.EndsWith("/api/like", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"song-" + host.Split('.')[0] + "\",\"liked\":" + (request.Method == HttpMethod.Post ? "true" : "false") + ",\"count\":3}");
            }

            if (path.EndsWith("/api/lyrics/current", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"songId\":\"s\",\"synced\":false,\"lines\":[{\"text\":\"" + host + "\"}]}");
            }

            if (path.EndsWith("/api/schedule", StringComparison.Ordinal))
            {
                var day = "[" + string.Join(',', Enumerable.Repeat("\"s1\"", 24)) + "]";
                var grid = string.Join(',', Enumerable.Range(0, 7).Select(d => "\"" + d + "\":" + day));
                return Json(HttpStatusCode.OK, "{\"timezone\":\"UTC\",\"shows\":[{\"id\":\"s1\",\"name\":\"" + host + "\"}],\"schedule\":{" + grid + "}}");
            }

            if (path.Contains("/api/persona-avatar/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.Png(8, 8)) };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/request", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Accepted, "{\"success\":true,\"requestId\":\"req-" + host.Split('.')[0] + "\",\"status\":\"pending\"}");
            }

            if (path.Contains("/api/request/", StringComparison.Ordinal))
            {
                return RequestStatusNotFound.Contains(host)
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(HttpStatusCode.OK, "{\"status\":\"resolved\",\"ack\":\"from " + host + "\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
