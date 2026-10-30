using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L4: a station failure is recorded and logged as a fixed category, never the exception's text (which can name the
/// host and port) or a URL.
/// </summary>
public class StationFailuresTests
{
    [Fact]
    public void Each_failure_maps_to_its_fixed_category()
    {
        Assert.Equal(StationFailures.TimedOut, StationFailures.Category(new TaskCanceledException("HttpClient.Timeout of 8 seconds")));
        Assert.Equal(StationFailures.Redirected, StationFailures.Category(new StationRedirectException(HttpStatusCode.Found, new Uri("http://elsewhere.example/api/"))));
        Assert.Equal("HTTP 503", StationFailures.Category(new HttpRequestException("Service Unavailable at radio.example", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(StationFailures.CouldNotConnect, StationFailures.Category(new HttpRequestException("Connection refused (radio.example:7700)")));
        Assert.Equal(StationFailures.NotJson, StationFailures.Category(new JsonException("'<' is an invalid start of a value")));
        Assert.Equal(StationFailures.NotJson, StationFailures.Category(new NotSupportedException("text/html")));
        Assert.Equal(StationFailures.Unexpected, StationFailures.Category(new InvalidOperationException("radio.example")));
    }

    [Fact]
    public void Only_station_failures_count_as_such()
    {
        Assert.True(StationFailures.IsStationFailure(new HttpRequestException("x")));
        Assert.True(StationFailures.IsStationFailure(new TaskCanceledException("x")));
        Assert.True(StationFailures.IsStationFailure(new JsonException("x")));
        Assert.True(StationFailures.IsStationFailure(new NotSupportedException("x")));
        Assert.False(StationFailures.IsStationFailure(new InvalidOperationException("x")));
        Assert.False(StationFailures.IsStationFailure(new ArgumentException("x")));
    }
}
