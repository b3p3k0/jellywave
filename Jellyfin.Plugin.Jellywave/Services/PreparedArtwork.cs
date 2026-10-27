namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The outcome of <see cref="LiveItemArtwork.PrepareAsync"/>: which image the live item should show and, when it
/// is a cover that still has to be saved, its bytes.
/// </summary>
public sealed class PreparedArtwork
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreparedArtwork"/> class.
    /// </summary>
    /// <param name="origin">The station origin of the cover, or null for the logo.</param>
    /// <param name="key">The cover id, or <see cref="LiveItemArtwork.LogoKey"/>.</param>
    /// <param name="image">The downloaded cover, or null for the logo.</param>
    public PreparedArtwork(string? origin, string key, StationImage? image)
    {
        Origin = origin;
        Key = key;
        Image = image;
    }

    /// <summary>Gets the station origin (<see cref="StationUrls.Origin"/>) of the cover, or null for the embedded logo.</summary>
    public string? Origin { get; }

    /// <summary>Gets the cover id, or <see cref="LiveItemArtwork.LogoKey"/> for the embedded logo.</summary>
    public string Key { get; }

    /// <summary>Gets the downloaded cover, or null when the logo should be used.</summary>
    public StationImage? Image { get; }
}
