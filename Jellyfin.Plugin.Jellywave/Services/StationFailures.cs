using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The fixed categories a station poll failure is recorded and logged under (card L4). A category never carries a URL,
/// a host or exception text: <see cref="HttpRequestException"/> messages can name the host and port, and the admin health
/// endpoint and the log line name the station by its configured name instead.
/// </summary>
public static class StationFailures
{
    /// <summary>The station did not answer within the client's budget.</summary>
    public const string TimedOut = "timed out";

    /// <summary>The connection failed (refused, reset, no route, DNS).</summary>
    public const string CouldNotConnect = "could not connect";

    /// <summary>The station answered with a redirect, which the plugin does not follow (card 14).</summary>
    public const string Redirected = "answered with a redirect";

    /// <summary>The station answered with something that is not the now-playing JSON (a proxy error page).</summary>
    public const string NotJson = "did not answer with the expected JSON";

    /// <summary>The feed parsed as JSON <c>null</c>: the station answered, but with nothing to show (card 17).</summary>
    public const string EmptyFeed = "the station answered with an empty feed";

    /// <summary>A bug or an unexpected condition in the plugin while polling the station; the server log has the detail.</summary>
    public const string Unexpected = "unexpected error; see the server log";

    /// <summary>Prefix of an HTTP error status; the status code follows.</summary>
    private const string HttpStatusPrefix = "HTTP ";

    /// <summary>
    /// Gets the category of a station failure.
    /// </summary>
    /// <param name="exception">The exception the station call threw.</param>
    /// <returns>The fixed category.</returns>
    public static string Category(Exception exception)
        => exception switch
        {
            OperationCanceledException => TimedOut,
            StationRedirectException => Redirected,
            HttpRequestException { StatusCode: { } status } => HttpStatusPrefix + ((int)status).ToString(CultureInfo.InvariantCulture),
            HttpRequestException => CouldNotConnect,
            JsonException or NotSupportedException => NotJson,
            _ => Unexpected
        };

    /// <summary>
    /// Says whether an exception means "the station did not give us a feed", as opposed to a bug. A non-JSON 200 (a proxy
    /// error page) surfaces as <see cref="JsonException"/> on .NET 10 and as <see cref="NotSupportedException"/> on runtimes
    /// that validate the content type; the family is caught.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for a station failure.</returns>
    public static bool IsStationFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException;
}
