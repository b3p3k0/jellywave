namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// What kind of station a <see cref="StationDefinition"/> describes.
/// </summary>
public enum StationKind
{
    /// <summary>
    /// A SUB/WAVE station: base URL plus stream path under the card 14 URL rules, with now playing, covers, the station
    /// page, likes, lyrics, schedule and requests.
    /// </summary>
    SubWave = 0,

    /// <summary>
    /// A plain audio stream given by one whole URL: playable item, name and logo only.
    /// </summary>
    Stream = 1
}
