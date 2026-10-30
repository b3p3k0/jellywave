using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a: what <see cref="StationCalls"/> keeps. Lyrics 10 min for lines, 15 s for errors and "pending", 64 per station;
/// portraits 10 min, 32 and 8 MB in all; like status 5 s per song on air, dropped after a like and when the song changes;
/// request status 8 s per receipt. A cached answer costs no budget and no permit.
/// </summary>
public class StationCallsCacheTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri One = new("http://one.example/");
    private static readonly StationKey KeyOne = StationKey.Of("one", One);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Setup
    {
        public Setup()
        {
            Budgets = new UserBudgets(Clock);
            Calls = new StationCalls(new StationClient(new Factory(Station)), Budgets, Limiter, NullLogger<StationCalls>.Instance, Clock);
        }

        public StationCallsTests.Station Station { get; } = new();

        public UserBudgetsTests.ManualClock Clock { get; } = new();

        public UserBudgets Budgets { get; }

        public StationCallLimiter Limiter { get; } = new();

        public StationCalls Calls { get; }

        public int Asked(string path) => Station.Seen.Count(s => s.EndsWith(" " + path, StringComparison.Ordinal));

        public void SpendBudget(Guid user)
        {
            while (Budgets.TryReserve(user, StationCallKind.Lyrics, out _) is not null)
            {
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => StationCallsTests.Station.Json(status, body);

    [Fact]
    public async Task Lyrics_lines_are_kept_ten_minutes_and_cost_another_user_nothing()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"lines\":[{\"text\":\"la\"}]}"));

        Assert.Equal("la", (await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None)).Value!.Body!.Lines![0].Text);
        s.SpendBudget(Bob);
        var cached = await s.Calls.GetLyricsAsync(Bob, KeyOne, One, "song", CancellationToken.None);

        Assert.Equal(0, cached.Refusal);   // served from the cache: no budget needed
        Assert.Equal("la", cached.Value!.Body!.Lines![0].Text);
        Assert.Equal(1, s.Asked("/api/lyrics/current"));

        s.Clock.Now += StationCalls.LyricsLifetime - TimeSpan.FromSeconds(1);
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        Assert.Equal(1, s.Asked("/api/lyrics/current"));
        s.Clock.Now += TimeSpan.FromSeconds(1);
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/lyrics/current"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"pending\":true,\"lines\":[]}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":\"music server down\"}")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    public async Task Pending_and_error_lyrics_are_kept_fifteen_seconds(HttpStatusCode status, string body)
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(status, body));

        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        s.Clock.Now += StationCalls.ErrorLifetime - TimeSpan.FromSeconds(1);
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        Assert.Equal(1, s.Asked("/api/lyrics/current"));

        s.Clock.Now += TimeSpan.FromSeconds(1);
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/lyrics/current"));
    }

    [Fact]
    public async Task No_answer_is_kept_fifteen_seconds()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "lost");

        Assert.False((await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None)).Answered);
        Assert.False((await s.Calls.GetLyricsAsync(Bob, KeyOne, One, "song", CancellationToken.None)).Answered);
        Assert.Equal(1, s.Asked("/api/lyrics/current"));

        s.Clock.Now += StationCalls.ErrorLifetime;
        await s.Calls.GetLyricsAsync(Bob, KeyOne, One, "song", CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/lyrics/current"));
    }

    [Fact]
    public async Task At_most_sixty_four_lyrics_per_station()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"lines\":[{\"text\":\"la\"}]}"));
        for (var i = 0; i < StationCalls.LyricsPerStation + 10; i++)
        {
            await s.Calls.GetLyricsAsync(Guid.NewGuid(), KeyOne, One, "song" + i, CancellationToken.None);
        }

        Assert.Equal(StationCalls.LyricsPerStation, s.Calls.Lyrics.Count);
    }

    [Fact]
    public async Task Portraits_are_kept_ten_minutes_bounded_to_thirty_two_and_eight_megabytes()
    {
        var s = new Setup();
        var small = ImageFixtures.Png(64, 64);
        s.Station.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(small) });
        for (var i = 0; i < StationCalls.MaxAvatars + 5; i++)
        {
            await s.Calls.GetAvatarAsync(Guid.NewGuid(), KeyOne, One, "dj_" + i, CancellationToken.None);
        }

        Assert.Equal(StationCalls.MaxAvatars, s.Calls.Avatars.Count);
        var asked = s.Asked("/api/persona-avatar/dj_" + (StationCalls.MaxAvatars + 4));
        s.Clock.Now += StationCalls.AvatarLifetime - TimeSpan.FromSeconds(1);
        await s.Calls.GetAvatarAsync(Alice, KeyOne, One, "dj_" + (StationCalls.MaxAvatars + 4), CancellationToken.None);
        Assert.Equal(asked, s.Asked("/api/persona-avatar/dj_" + (StationCalls.MaxAvatars + 4)));

        // Large portraits: the byte bound holds before the count does.
        var large = LargePng(1536 * 1024);
        s.Station.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(large) });
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull((await s.Calls.GetAvatarAsync(Guid.NewGuid(), KeyOne, One, "big_" + i, CancellationToken.None)).Value);
        }

        Assert.InRange(s.Calls.Avatars.Bytes, 1, StationCalls.MaxAvatarBytes);
        Assert.InRange(s.Calls.Avatars.Count, 1, (int)(StationCalls.MaxAvatarBytes / large.Length));
    }

    [Fact]
    public async Task Like_status_is_kept_five_seconds_for_the_song_on_air_and_shared_by_every_user()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"s1\",\"liked\":false,\"count\":3}"));

        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None);
        await s.Calls.GetLikeAsync(Bob, KeyOne, One, "s1", CancellationToken.None);
        Assert.Equal(1, s.Asked("/api/like"));

        s.Clock.Now += StationCalls.LikeLifetime;
        await s.Calls.GetLikeAsync(Bob, KeyOne, One, "s1", CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/like"));
    }

    [Fact]
    public async Task Like_status_is_dropped_when_the_song_changes_and_after_a_like_is_taken()
    {
        var s = new Setup();
        var song = "s1";
        s.Station.Answer = (r, _) => Task.FromResult(r.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, "{\"ok\":true,\"songId\":\"" + song + "\",\"liked\":true,\"count\":4}")
            : Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"" + song + "\",\"liked\":false,\"count\":3}"));

        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None);
        Assert.Equal(1, s.Calls.Likes.Count);

        song = "s2";
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s2", CancellationToken.None);
        Assert.Equal(1, s.Calls.Likes.Count);   // s1's status went with the song
        Assert.Equal(2, s.Asked("/api/like"));

        await s.Calls.PostLikeAsync(Alice, KeyOne, One, "s2", CancellationToken.None);
        Assert.Equal(0, s.Calls.Likes.Count);
        await s.Calls.GetLikeAsync(Bob, KeyOne, One, "s2", CancellationToken.None);
        Assert.Equal(3, s.Station.Seen.Count(x => x.EndsWith("GET /api/like", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_like_keeps_the_status_and_an_answer_for_another_song_is_not_kept()
    {
        var s = new Setup();
        s.Station.Answer = (r, _) => Task.FromResult(r.Method == HttpMethod.Post
            ? Json(HttpStatusCode.TooManyRequests, "{\"error\":\"too fast\",\"retryAfter\":5}")
            : Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"s1\",\"liked\":false,\"count\":3}"));

        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None);
        await s.Calls.PostLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None);
        Assert.Equal(1, s.Calls.Likes.Count);

        // The poller heard s2, the station still answers s1: not kept as s2's status.
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s2", CancellationToken.None);
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s2", CancellationToken.None);
        Assert.Equal(0, s.Calls.Likes.Count);
        Assert.Equal(3, s.Station.Seen.Count(x => x.EndsWith("GET /api/like", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_song_on_air_the_like_status_is_asked_every_time()
    {
        var s = new Setup();
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, null, CancellationToken.None);
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, null, CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/like"));
        Assert.Equal(0, s.Calls.Likes.Count);
    }

    [Fact]
    public async Task Request_status_is_kept_eight_seconds_per_receipt()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"));

        for (var i = 0; i < 2; i++)
        {
            await s.Calls.GetRequestStatusAsync(Alice, KeyOne, One, "token-a", "req_1", CancellationToken.None);
        }

        await s.Calls.GetRequestStatusAsync(Bob, KeyOne, One, "token-b", "req_1", CancellationToken.None);
        Assert.Equal(2, s.Asked("/api/request/req_1"));   // one per receipt

        s.Clock.Now += StationCalls.RequestStatusLifetime;
        await s.Calls.GetRequestStatusAsync(Alice, KeyOne, One, "token-a", "req_1", CancellationToken.None);
        Assert.Equal(3, s.Asked("/api/request/req_1"));
    }

    [Fact]
    public async Task Concurrent_misses_for_one_song_are_one_station_call_one_permit_and_one_budget_slot()
    {
        var s = new Setup();
        var release = new TaskCompletionSource();
        s.Station.Answer = async (_, _) =>
        {
            await release.Task;
            return Json(HttpStatusCode.OK, "{\"lines\":[{\"text\":\"la\"}]}");
        };

        var users = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        var calls = users.Select(u => s.Calls.GetLyricsAsync(u, KeyOne, One, "song", CancellationToken.None)).ToList();
        Assert.Equal(1, s.Limiter.InUse);
        release.SetResult();
        var answers = await Task.WhenAll(calls);

        Assert.All(answers, a => Assert.Equal("la", a.Value!.Body!.Lines![0].Text));
        Assert.Equal(1, s.Asked("/api/lyrics/current"));
        Assert.Equal(0, s.Limiter.InUse);

        // Only the first user's budget paid: every other user still has the whole minute.
        var spent = users.Select(u => Enumerable.Range(0, 100).Count(i => s.Budgets.TryReserve(u, StationCallKind.Lyrics, out _) is not null)).ToList();
        Assert.Equal(UserBudgets.CallsPerMinute - 1, spent[0]);
        Assert.All(spent.Skip(1), n => Assert.Equal(UserBudgets.CallsPerMinute, n));
    }

    [Fact]
    public async Task A_cached_answer_is_served_only_after_the_route_s_gate_and_resolution()
    {
        // The cache sits behind the filters: a station id the filter refuses gets its 404 even with the answer kept.
        var h = new MultiStationHarness();
        var calls = new StationCalls(h.Client, NullLogger<StationCalls>.Instance);
        var ok = await WebRoute.RunAsync(h.Controller(calls: calls), nameof(JellywaveWebController.Lyrics), ("stationId", "two"), ("songId", "song-x"));
        Assert.Equal(StatusCodes.Status200OK, WebRoute.StatusOf(ok));
        var asked = h.Seen.Count;

        foreach (var refused in MultiStationHarness.UnlistedIds().Select(row => (string?)row[0]))
        {
            var result = await WebRoute.RunAsync(h.Controller(calls: calls), nameof(JellywaveWebController.Lyrics), ("stationId", refused), ("songId", "song-x"));
            Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        }

        Assert.Equal(asked, h.Seen.Count);
    }

    [Fact]
    public async Task The_page_s_busiest_minute_costs_a_user_under_a_third_of_the_budget()
    {
        // The minute stationPage.test.js measures ("R2a a busy minute of page use"): 35 page requests. Replayed here at the
        // page's own intervals against the real caches: lyrics panel open with lyrics pending all minute (asked again every
        // 8 s), a song change at 30 s (the feed tick sees it at 35 s), one like, one request pending all minute (polled every
        // 3 s), one portrait. 17 station calls: 7 request status, 5 lyrics, 2 like status, 1 like, 1 request, 1 portrait.
        var s = new Setup();
        var song = "s1";
        s.Station.Answer = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/api/like" when r.Method == HttpMethod.Post => Json(HttpStatusCode.OK, "{\"ok\":true,\"songId\":\"" + song + "\",\"liked\":true}"),
            "/api/like" => Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"" + song + "\"}"),
            "/api/lyrics/current" => Json(HttpStatusCode.OK, "{\"pending\":true,\"lines\":[]}"),
            "/api/request" => Json(HttpStatusCode.Accepted, "{\"requestId\":\"req_1\"}"),
            "/api/request/req_1" => Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.Png(64, 64)) }
        });
        var start = s.Clock.Now;
        var events = new List<(double At, Func<Task> Call)>
        {
            (0, () => s.Calls.GetAvatarAsync(Alice, KeyOne, One, "dj_one", CancellationToken.None)),
            (0, () => s.Calls.GetLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None)),
            (0, () => s.Calls.PostLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None)),
            (0, () => s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "play something", "A", () => true, CancellationToken.None)),
            (35, () => s.Calls.GetLikeAsync(Alice, KeyOne, One, "s2", CancellationToken.None)),
        };
        for (var t = 0; t <= 32; t += 8)
        {
            events.Add((t, () => s.Calls.GetLyricsAsync(Alice, KeyOne, One, "s1", CancellationToken.None)));
        }

        for (var t = 35; t < 60; t += 8)
        {
            events.Add((t, () => s.Calls.GetLyricsAsync(Alice, KeyOne, One, "s2", CancellationToken.None)));
        }

        for (var t = 0; t <= 60; t += 3)
        {
            events.Add((t, () => s.Calls.GetRequestStatusAsync(Alice, KeyOne, One, "token", "req_1", CancellationToken.None)));
        }

        foreach (var (at, call) in events.OrderBy(e => e.At))
        {
            s.Clock.Now = start + TimeSpan.FromSeconds(at);
            song = at >= 30 ? "s2" : "s1";
            await call();
        }

        // Every station call was Alice's (no other user shares her caches here), so the station saw what her budget paid.
        Assert.Equal(35, events.Count);
        Assert.InRange(s.Station.Seen.Count, 1, (UserBudgets.CallsPerMinute / 3) - 1);
        Assert.Equal(7, s.Asked("/api/request/req_1"));
        Assert.Equal(5, s.Asked("/api/lyrics/current"));
        Assert.Equal(17, s.Station.Seen.Count);
    }

    /// <summary>The same station id with its base URL changed: another origin, so another key.</summary>
    private static readonly Uri Moved = new("http://moved.example/");

    private static readonly StationKey KeyMoved = StationKey.Of("one", Moved);

    [Fact]
    public async Task Lyrics_are_asked_again_when_the_station_s_base_url_changes_under_its_id()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"lines\":[{\"text\":\"la\"}]}"));
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);
        await s.Calls.GetLyricsAsync(Alice, KeyMoved, Moved, "song", CancellationToken.None);
        await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "song", CancellationToken.None);

        Assert.Equal(new[] { "one.example GET /api/lyrics/current", "moved.example GET /api/lyrics/current" }, s.Station.Seen);
    }

    [Fact]
    public async Task Like_status_is_asked_again_when_the_station_s_base_url_changes_under_its_id()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"enabled\":true,\"songId\":\"s1\"}"));
        await s.Calls.GetLikeAsync(Alice, KeyOne, One, "s1", CancellationToken.None);
        await s.Calls.GetLikeAsync(Alice, KeyMoved, Moved, "s1", CancellationToken.None);

        Assert.Equal(new[] { "one.example GET /api/like", "moved.example GET /api/like" }, s.Station.Seen);
    }

    [Fact]
    public async Task A_portrait_is_asked_again_when_the_station_s_base_url_changes_under_its_id()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.Png(64, 64)) });
        await s.Calls.GetAvatarAsync(Alice, KeyOne, One, "dj_one", CancellationToken.None);
        await s.Calls.GetAvatarAsync(Alice, KeyMoved, Moved, "dj_one", CancellationToken.None);

        Assert.Equal(new[] { "one.example GET /api/persona-avatar/dj_one", "moved.example GET /api/persona-avatar/dj_one" }, s.Station.Seen);
    }

    [Fact]
    public async Task A_request_status_is_asked_again_when_the_station_s_base_url_changes_under_its_id()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"));
        await s.Calls.GetRequestStatusAsync(Alice, KeyOne, One, "token", "req_1", CancellationToken.None);
        await s.Calls.GetRequestStatusAsync(Alice, KeyMoved, Moved, "token", "req_1", CancellationToken.None);

        Assert.Equal(new[] { "one.example GET /api/request/req_1", "moved.example GET /api/request/req_1" }, s.Station.Seen);
    }

    private static byte[] LargePng(int size)
    {
        var bytes = new List<byte>(ImageFixtures.Png(64, 64)[..^12]);   // without IEND
        ImageFixtures.PngChunk(bytes, "IDAT", new byte[size]);
        ImageFixtures.PngChunk(bytes, "IEND", []);
        return [.. bytes];
    }
}
