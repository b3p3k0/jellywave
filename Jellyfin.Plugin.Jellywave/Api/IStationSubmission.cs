namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// A station route's POST body: it names the station it is for (card L5). <see cref="StationRouteAttribute"/> reads it.
/// </summary>
public interface IStationSubmission
{
    /// <summary>Gets the station id from the station list; null means the primary station (an older cached page sends none).</summary>
    string? StationId { get; }
}
