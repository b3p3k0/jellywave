using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What everything learned from a SUB/WAVE station is kept under (card L4): the station id and the station origin
/// (<see cref="StationUrls.Origin"/>). The caches hold one entry per station id; an entry whose origin differs from the
/// key's is another station's data (the base URL changed under the same id), so it is never served and the next write
/// replaces it, exactly as card 17's origin key did for the single station.
/// </summary>
public sealed record StationKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StationKey"/> class.
    /// </summary>
    /// <param name="stationId">The station id (the item's external id).</param>
    /// <param name="origin">The station origin (<see cref="StationUrls.Origin"/>).</param>
    public StationKey(string stationId, string origin)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(origin);
        StationId = stationId;
        Origin = origin;
    }

    /// <summary>Gets the station id.</summary>
    public string StationId { get; }

    /// <summary>Gets the station origin: the parsed base URL in full.</summary>
    public string Origin { get; }

    /// <summary>
    /// Builds the key of a station from its id and parsed base URL.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="baseUrl">The parsed base URL (<see cref="StationUrls.TryParseBaseUrl"/>).</param>
    /// <returns>The key.</returns>
    public static StationKey Of(string stationId, Uri baseUrl) => new(stationId, StationUrls.Origin(baseUrl));
}
