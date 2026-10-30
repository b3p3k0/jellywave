using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// Moves a 0.4.x configuration onto the Live Radio shape (card L1, diagram 9) and keeps the rename bridge's list of
/// earlier tile names. Pure functions: the caller supplies the channel-id rule (Jellyfin's <c>GetNewItemId</c>) and saves.
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// The 0.4.x tile name when <see cref="PluginConfiguration.StationName"/> was blank.
    /// </summary>
    public const string LegacyFallbackTileName = "SUB/WAVE";

    /// <summary>
    /// The id the station migrated from a 0.4.x configuration had before card R2a (the external id of the single live item
    /// before Live Radio). It is the same on every install, so it is one of <see cref="StationIds.KnownPublicIds"/>: the
    /// migration now gives a random id, and a station that still has this one is re-keyed at startup.
    /// </summary>
    public const string LegacyMigratedStationId = "live";

    /// <summary>
    /// Gets the tile name a 0.4.x configuration produced: <see cref="PluginConfiguration.StationName"/> trimmed, or
    /// "SUB/WAVE" when blank (0.4.2.0's <c>JellywaveChannel.StationName</c>).
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The legacy tile name.</returns>
    public static string LegacyTileName(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.StationName) ? LegacyFallbackTileName : config.StationName.Trim();
    }

    /// <summary>
    /// Gets the tile name a configuration produces: <see cref="PluginConfiguration.LibraryName"/> trimmed, or
    /// <see cref="PluginConfiguration.DefaultLibraryName"/> when blank.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The tile name.</returns>
    public static string TileName(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.LibraryName) ? PluginConfiguration.DefaultLibraryName : config.LibraryName.Trim();
    }

    /// <summary>
    /// Migrates a configuration below <see cref="PluginConfiguration.CurrentSchemaVersion"/>. A non-empty legacy base URL
    /// becomes a SUB/WAVE station with a random id (<see cref="StationIds.NewId"/>; card R2a), unless the list already
    /// holds a station with the 0.4.x id <c>live</c> or a SUB/WAVE station with the same base URL; the legacy tile name is recorded as a previous channel name whether or not a URL was
    /// set (0.4.x showed an empty tile too); the tile is renamed to "Live Radio". The legacy fields are left as they
    /// are, so 0.4.2.0 still reads the file after a downgrade. A 0.4.x Direct station is relayed like every station (card
    /// L10): its <c>Delivery</c> and <c>ClientStreamUrl</c> are not read, and a downgrade finds Relay, 0.4.x's default.
    /// Idempotent: a migrated configuration is not touched.
    /// </summary>
    /// <param name="config">The configuration, changed in place.</param>
    /// <param name="channelId">Derives a channel id from a name, the way Jellyfin does.</param>
    /// <returns>True when the configuration changed and should be saved.</returns>
    public static bool Migrate(PluginConfiguration config, Func<string, Guid> channelId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(channelId);
        if (config.SchemaVersion >= PluginConfiguration.CurrentSchemaVersion)
        {
            return false;
        }

        var legacyTile = LegacyTileName(config);

        // Every id is derived before anything changes: a failing id rule throws here and leaves the configuration exactly as
        // loaded (the plugin then runs it unmigrated, see Plugin), never half migrated with the tile renamed but no bridge.
        foreach (var name in (config.PreviousChannelNames ?? []).Append(legacyTile).Append(PluginConfiguration.DefaultLibraryName))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _ = channelId(name.Trim());
            }
        }

        config.Stations ??= new Collection<StationDefinition>();
        config.PreviousChannelNames ??= new Collection<string>();

        if (!string.IsNullOrWhiteSpace(config.StationBaseUrl)
            && !config.Stations.Any(s => s is not null
                && (string.Equals(s.Id, LegacyMigratedStationId, StringComparison.Ordinal)
                    || (s.Kind == StationKind.SubWave && string.Equals(s.BaseUrl?.Trim(), config.StationBaseUrl.Trim(), StringComparison.Ordinal)))))
        {
            config.Stations.Insert(0, new StationDefinition
            {
                Id = StationIds.NewId(),
                Kind = StationKind.SubWave,
                Name = legacyTile,
                Enabled = true,
                SortOrder = 0,
                BaseUrl = config.StationBaseUrl,
                StreamPath = config.StreamPath ?? StationDefinition.DefaultStreamPath,
                Tagline = config.StationTagline ?? string.Empty,
                Codec = StationDefinition.DefaultFormat,
                Container = StationDefinition.DefaultFormat,
                BitrateKbps = config.StreamBitrateKbps,
                SampleRate = config.StreamSampleRate,
                Channels = config.StreamChannels
            });
        }

        config.LibraryName = PluginConfiguration.DefaultLibraryName;
        AddPreviousName(config, legacyTile, channelId);
        config.SchemaVersion = PluginConfiguration.CurrentSchemaVersion;
        return true;
    }

    /// <summary>
    /// Prepares a configuration posted for saving. A body below <see cref="PluginConfiguration.CurrentSchemaVersion"/> is
    /// migrated like a file only when it carries nothing of the new shape (no station and no tile name other than the
    /// default); otherwise its tile name and stations are kept as posted and it is marked current, so the next start does
    /// not migrate it again. Then <see cref="CaptureRename"/> keeps the earlier and carried tile names.
    /// </summary>
    /// <param name="current">The configuration in effect before the save.</param>
    /// <param name="incoming">The configuration being saved, changed in place.</param>
    /// <param name="channelId">Derives a channel id from a name, the way Jellyfin does.</param>
    public static void PrepareSave(PluginConfiguration current, PluginConfiguration incoming, Func<string, Guid> channelId)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.SchemaVersion < PluginConfiguration.CurrentSchemaVersion)
        {
            var legacyShape = (incoming.Stations is null || incoming.Stations.Count == 0)
                && string.Equals(TileName(incoming), PluginConfiguration.DefaultLibraryName, StringComparison.Ordinal);
            if (legacyShape)
            {
                Migrate(incoming, channelId);
            }
            else
            {
                incoming.Stations ??= new Collection<StationDefinition>();
                incoming.SchemaVersion = PluginConfiguration.CurrentSchemaVersion;
            }
        }

        CaptureRename(current, incoming, channelId);
    }

    /// <summary>
    /// Records the tile names a configuration save leaves behind (server side, in <c>Plugin.UpdateConfiguration</c>):
    /// every previous name the current configuration knows is kept, so a page or API client that drops the list cannot
    /// lift the old blocks, and the outgoing tile name is added when the tile is renamed. The carried names
    /// (<see cref="PluginConfiguration.CarriedChannelNames"/>) are kept the same way, so a carry-over is never proposed twice.
    /// </summary>
    /// <param name="current">The configuration in effect before the save.</param>
    /// <param name="incoming">The configuration being saved, changed in place.</param>
    /// <param name="channelId">Derives a channel id from a name, the way Jellyfin does.</param>
    /// <returns>True when <paramref name="incoming"/> changed.</returns>
    public static bool CaptureRename(PluginConfiguration current, PluginConfiguration incoming, Func<string, Guid> channelId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(channelId);
        incoming.PreviousChannelNames ??= new Collection<string>();

        var changed = false;
        foreach (var name in current.PreviousChannelNames ?? [])
        {
            changed |= AddPreviousName(incoming, name, channelId);
        }

        changed |= AddPreviousName(incoming, TileName(current), channelId);

        // The configuration in effect could not be migrated (it runs as loaded, schema 0): its 0.4.x tile name is an
        // earlier name only implicitly (RenameBridge.PreviousIds). Record it now, or this save would lift the old blocks.
        if (current.SchemaVersion < PluginConfiguration.CurrentSchemaVersion)
        {
            changed |= AddPreviousName(incoming, LegacyTileName(current), channelId);
        }

        foreach (var name in current.CarriedChannelNames ?? [])
        {
            changed |= AddCarriedName(incoming, name, channelId);
        }

        return changed;
    }

    /// <summary>
    /// Records that "Give them access to the renamed tile" ran for an earlier tile name, unless it is blank or a name with the same id is
    /// already recorded.
    /// </summary>
    /// <param name="config">The configuration, changed in place.</param>
    /// <param name="name">The earlier tile name.</param>
    /// <param name="channelId">Derives a channel id from a name, the way Jellyfin does.</param>
    /// <returns>True when the name was added.</returns>
    public static bool AddCarriedName(PluginConfiguration config, string? name, Func<string, Guid> channelId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(channelId);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        var id = channelId(trimmed);
        config.CarriedChannelNames ??= new Collection<string>();
        if (config.CarriedChannelNames.Any(n => !string.IsNullOrWhiteSpace(n) && channelId(n.Trim()) == id))
        {
            return false;
        }

        config.CarriedChannelNames.Add(trimmed);
        return true;
    }

    /// <summary>
    /// Adds a name to <see cref="PluginConfiguration.PreviousChannelNames"/> unless it is blank, derives the id of the
    /// configuration's own tile, or derives the id of a name already listed.
    /// </summary>
    /// <param name="config">The configuration, changed in place.</param>
    /// <param name="name">The name to record.</param>
    /// <param name="channelId">Derives a channel id from a name, the way Jellyfin does.</param>
    /// <returns>True when the name was added.</returns>
    public static bool AddPreviousName(PluginConfiguration config, string? name, Func<string, Guid> channelId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(channelId);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        var id = channelId(trimmed);
        config.PreviousChannelNames ??= new Collection<string>();
        if (id == channelId(TileName(config))
            || config.PreviousChannelNames.Any(n => !string.IsNullOrWhiteSpace(n) && channelId(n.Trim()) == id))
        {
            return false;
        }

        config.PreviousChannelNames.Add(trimmed);
        return true;
    }
}
