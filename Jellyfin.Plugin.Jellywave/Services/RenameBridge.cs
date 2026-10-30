using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The rename bridge (card L1): Jellyfin knows a channel only by the id it derives from the name, so a renamed tile is a
/// new channel, and a block list naming the old id no longer applies to it. The configuration keeps the earlier tile
/// names (<see cref="PluginConfiguration.PreviousChannelNames"/>); this derives their ids when asked, with Jellyfin's own
/// rule, and a block on any of them keeps the user away from the tile (<c>IsEnabledFor</c>) and the station routes
/// (<see cref="StationAccess"/>). It writes nothing.
/// </summary>
public sealed class RenameBridge
{
    private readonly Func<string, Guid> _channelId;
    private readonly Func<IReadOnlyCollection<Guid>> _otherChannelIds;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameBridge"/> class (DI constructor).
    /// </summary>
    /// <param name="finder">The finder, whose id derivation is Jellyfin's and which knows the other registered channels.</param>
    public RenameBridge(LiveItemFinder finder)
        : this(name => finder.DeriveChannelId(name), () => finder.OtherChannelIds())
    {
        ArgumentNullException.ThrowIfNull(finder);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameBridge"/> class with an explicit id rule and no other channels (tests).
    /// </summary>
    /// <param name="channelId">Derives a channel id from a name.</param>
    internal RenameBridge(Func<string, Guid> channelId)
        : this(channelId, () => [])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameBridge"/> class with explicit sources (tests).
    /// </summary>
    /// <param name="channelId">Derives a channel id from a name.</param>
    /// <param name="otherChannelIds">The ids of the other plugins' registered channels.</param>
    internal RenameBridge(Func<string, Guid> channelId, Func<IReadOnlyCollection<Guid>> otherChannelIds)
    {
        _channelId = channelId;
        _otherChannelIds = otherChannelIds;
    }

    /// <summary>
    /// Gets the id Jellyfin derives for the configuration's own tile name.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The current channel id.</returns>
    public Guid CurrentId(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _channelId(ConfigMigration.TileName(config));
    }

    /// <summary>
    /// Gets the ids of the earlier tile names, without duplicates and without the current tile's own id: the hiding side
    /// (<c>IsEnabledFor</c>, <see cref="StationAccess"/>), which uses every earlier name. A configuration the plugin could
    /// not migrate (<see cref="PluginConfiguration.SchemaVersion"/> 0, in memory only) adds its 0.4.x tile name, so a
    /// failed migration still keeps old blocks.
    /// </summary>
    /// <param name="config">The configuration, or null when the plugin is not loaded (no previous ids).</param>
    /// <returns>The previous channel ids.</returns>
    public IReadOnlyList<Guid> PreviousIds(PluginConfiguration? config)
    {
        if (config is null)
        {
            return [];
        }

        IEnumerable<string> names = config.PreviousChannelNames ?? [];
        if (config.SchemaVersion < PluginConfiguration.CurrentSchemaVersion)
        {
            names = names.Append(ConfigMigration.LegacyTileName(config));
        }

        var list = names.ToArray();
        if (list.Length == 0)
        {
            return [];
        }

        var current = CurrentId(config);
        return list
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => _channelId(n.Trim()))
            .Where(id => id != current)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Gets the earlier tile names whose ids differ from the current tile's, one per id, in the order recorded.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The names, trimmed.</returns>
    public IReadOnlyList<string> PreviousNames(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var current = CurrentId(config);
        var seen = new HashSet<Guid>();
        return (config.PreviousChannelNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(n => _channelId(n) is var id && id != current && seen.Add(id))
            .ToArray();
    }

    /// <summary>
    /// Gets what "Give them access to the renamed tile" may still act on: the earlier tile names (one per id, current id excluded) that it has
    /// not run for yet (<see cref="PluginConfiguration.CarriedChannelNames"/>), minus those whose id is now another
    /// plugin's registered channel: copying a grant or block of that id would act on the other channel's users. Those
    /// are only counted; the hiding side keeps them.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The names and ids to carry, and how many earlier names were skipped for belonging to another channel.</returns>
    public (IReadOnlyList<string> Names, IReadOnlyList<Guid> Ids, int SkippedOtherChannel) CarryCandidates(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var earlier = PreviousNames(config);
        if (earlier.Count == 0)
        {
            return ([], [], 0);
        }

        var carried = (config.CarriedChannelNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => _channelId(n.Trim()))
            .ToHashSet();
        var others = _otherChannelIds();
        var names = new List<string>();
        var ids = new List<Guid>();
        var skipped = 0;
        foreach (var name in earlier)
        {
            var id = _channelId(name);
            if (carried.Contains(id))
            {
                continue;
            }

            if (others.Contains(id))
            {
                skipped++;
                continue;
            }

            names.Add(name);
            ids.Add(id);
        }

        return (names, ids, skipped);
    }

    /// <summary>
    /// Whether a user's block list names any of the given channel ids. Pure; no I/O.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ids">The channel ids.</param>
    /// <returns>True when blocked from one of them.</returns>
    public static bool BlocksAny(User user, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Count != 0 && user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels).Any(ids.Contains);
    }

    /// <summary>
    /// Whether a user's block list names an earlier tile of this configuration.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="config">The configuration, or null when the plugin is not loaded.</param>
    /// <returns>True when the user is blocked from a previous tile.</returns>
    public bool BlocksPrevious(User user, PluginConfiguration? config) => BlocksAny(user, PreviousIds(config));
}
