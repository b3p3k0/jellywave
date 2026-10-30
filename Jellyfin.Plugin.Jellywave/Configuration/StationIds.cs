using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// Station ids that nobody outside the server can guess (card R2a, HI decision B). Jellyfin derives a channel item's id
/// from the station id, the tile name and fixed text (<c>MD5(UTF-16LE(type + external id + channel name + "16"))</c>), and
/// its <c>/Audio/{id}/stream</c> needs no sign-in, so an id that is the same on every install (the 0.4.x migration's
/// <c>live</c>, the starter stations' fixed ids before 0.5.0.0) lets anyone compute a playable item id. New stations,
/// seeds and migrations get a random id (<see cref="NewId"/>), and <see cref="Rekey"/> replaces the known public ones once,
/// at startup.
/// </summary>
public static class StationIds
{
    /// <summary>
    /// The station ids every install could share: <c>live</c> (the station migrated from 0.4.x) and the five fixed ids the
    /// starter stations had before card R2a. Never used for a new station again.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownPublicIds = new[]
    {
        ConfigMigration.LegacyMigratedStationId,
        "4fdfcd25055c4a85a8a96686d9ee4582",
        "382c3ae96fcf455fb90c352050471d38",
        "596eda1d74de4a3486e2ba8f7e92892f",
        "280c071532884fde8e739a851fce7933",
        "172b37d4d9c547d386b242b7f937e2f5"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Makes a new random station id: 32 lowercase hex digits, the shape the configuration page gives a station it adds.
    /// </summary>
    /// <returns>The id.</returns>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gives every station whose id is in <see cref="KnownPublicIds"/> a new random id. Stations that shared one public id
    /// (a duplicate, refused as such) share its one new id, so the duplicate stays a duplicate. A configuration without a
    /// public id is not touched, so a re-keyed configuration is never re-keyed again.
    /// </summary>
    /// <param name="config">The configuration, changed in place.</param>
    /// <returns>True when an id changed.</returns>
    public static bool Rekey(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var station in config.Stations ?? [])
        {
            if (station?.Id is not { } id || !KnownPublicIds.Contains(id))
            {
                continue;
            }

            if (!replaced.TryGetValue(id, out var fresh))
            {
                fresh = NewId();
                replaced[id] = fresh;
            }

            station.Id = fresh;
        }

        return replaced.Count > 0;
    }
}
