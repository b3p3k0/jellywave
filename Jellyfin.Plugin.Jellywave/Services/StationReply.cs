namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// A station answer where the status code carries meaning (the request endpoints refuse with 4xx/5xx and a JSON body).
/// </summary>
/// <typeparam name="T">The body type.</typeparam>
public sealed class StationReply<T>
    where T : class
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StationReply{T}"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="body">The parsed body, or null when there was none or it was not JSON.</param>
    public StationReply(int statusCode, T? body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the parsed body, or null.</summary>
    public T? Body { get; }
}
