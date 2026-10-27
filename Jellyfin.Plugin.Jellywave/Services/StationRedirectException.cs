using System;
using System.Net;
using System.Net.Http;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The station answered a JSON request with a redirect (3xx), which the plugin never follows. It is an
/// <see cref="HttpRequestException"/>, so every caller that handles a station failure handles this one too. The
/// message stays generic (it reaches the poller status); only the admin probe shows <see cref="Location"/>.
/// </summary>
public sealed class StationRedirectException : HttpRequestException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StationRedirectException"/> class.
    /// </summary>
    public StationRedirectException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationRedirectException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public StationRedirectException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationRedirectException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public StationRedirectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationRedirectException"/> class.
    /// </summary>
    /// <param name="statusCode">The redirect status the station answered.</param>
    /// <param name="location">Where the station pointed, resolved against the request URL, or null when it named no place.</param>
    public StationRedirectException(HttpStatusCode statusCode, Uri? location)
        : base("The station answered HTTP " + (int)statusCode + " (a redirect), which is not followed.", null, statusCode)
    {
        Location = location;
    }

    /// <summary>Gets where the station pointed, or null.</summary>
    public Uri? Location { get; }
}
