namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The outcome of <see cref="StationLogos.PrepareAsync"/>: the image a station's item should get, already downloaded
/// when it is the station's own logo.
/// </summary>
public sealed class PreparedLogo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreparedLogo"/> class.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="key">The logo key (<see cref="StationLogos.KeyOf"/>).</param>
    /// <param name="image">The downloaded logo, or null for the embedded logo.</param>
    public PreparedLogo(string stationId, string key, StationImage? image)
    {
        StationId = stationId;
        Key = key;
        Image = image;
    }

    /// <summary>Gets the station id.</summary>
    public string StationId { get; }

    /// <summary>Gets the logo key: station id, kind and logo URL.</summary>
    public string Key { get; }

    /// <summary>Gets the downloaded logo, or null when the embedded logo stands in.</summary>
    public StationImage? Image { get; }
}
