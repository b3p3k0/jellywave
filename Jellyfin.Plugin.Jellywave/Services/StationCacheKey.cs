namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What <see cref="StationCache{T}"/> keeps an answer under (card R2a): the station's key (its id and origin, the
/// <see cref="StationKey"/> rule, so a base URL changed under the same id never serves the old station's answer) and the
/// item asked for (a song id, a persona id, a receipt token).
/// </summary>
/// <param name="Station">The station's key.</param>
/// <param name="Item">The item at that station.</param>
public readonly record struct StationCacheKey(StationKey Station, string Item);
