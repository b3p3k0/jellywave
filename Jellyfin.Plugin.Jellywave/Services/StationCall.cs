using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The outcome of one user-driven station call through <see cref="StationCalls"/> (card R2a): refused by this server
/// before the station was asked (<see cref="Refusal"/> 429 or 503, with <see cref="RetryAfter"/>), not answered (a
/// timeout or a failed connection), or the station's answer.
/// </summary>
/// <typeparam name="T">The answer type.</typeparam>
public sealed class StationCall<T>
    where T : class
{
    private StationCall(T? value, bool answered, int refusal, TimeSpan retryAfter)
    {
        Value = value;
        Answered = answered;
        Refusal = refusal;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the station's answer; null when it did not answer or the call was refused here (an image with no art is null too).</summary>
    public T? Value { get; }

    /// <summary>Gets a value indicating whether the station answered.</summary>
    public bool Answered { get; }

    /// <summary>Gets the HTTP status this server refused the call with before asking the station (429 or 503), or 0.</summary>
    public int Refusal { get; }

    /// <summary>Gets how long the caller should wait before trying again, when refused.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>The station's answer.</summary>
    /// <param name="value">The answer.</param>
    /// <returns>The outcome.</returns>
    internal static StationCall<T> Of(T? value) => new(value, true, 0, TimeSpan.Zero);

    /// <summary>The station did not answer.</summary>
    /// <returns>The outcome.</returns>
    internal static StationCall<T> NoAnswer() => new(null, false, 0, TimeSpan.Zero);

    /// <summary>Refused here before the station was asked.</summary>
    /// <param name="status">429 or 503.</param>
    /// <param name="retryAfter">How long to wait.</param>
    /// <returns>The outcome.</returns>
    internal static StationCall<T> Refuse(int status, TimeSpan retryAfter) => new(null, false, status, retryAfter);
}
