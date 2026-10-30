namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The plain stream the title route's filter resolved (card L6, <see cref="StationRouteAttribute.PlainStream"/>): its id
/// and nothing else, so the route has no stream URL to hand on. Left in <c>HttpContext.Items</c> under
/// <see cref="StationRouteAttribute.PlainStationItem"/>, a key no SUB/WAVE route reads.
/// </summary>
public sealed class PlainStation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlainStation"/> class.
    /// </summary>
    /// <param name="stationId">The listed plain stream's id.</param>
    public PlainStation(string stationId)
    {
        StationId = stationId;
    }

    /// <summary>Gets the station id.</summary>
    public string StationId { get; }
}
