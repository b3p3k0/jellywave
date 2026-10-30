using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// The stations a fresh install starts with (card L9), so the tile plays out of the box. Public station URLs, checked
/// by hand when the list was written (PLAN card L9 records the checks and each station's listening terms); they pass the
/// same <c>StationUrls</c> and <c>StationSources</c> rules as a station the admin adds, and their logos come through the
/// plain-stream logo path at runtime (nothing is bundled). Only <c>Plugin</c> adds them, and only when there is no
/// configuration file at all: an upgrade, a migrated 0.4.x file and a list the admin emptied are never touched, so a
/// station the admin removes is never put back. SomaFM's Groove Salad was on the approved list but is left out: SomaFM's
/// terms of service grant no third-party clients and forbid re-streaming without written permission (card L9).
/// </summary>
public static class StarterStations
{
    /// <summary>
    /// Gets new copies of the starter stations, in grid order (<see cref="StationDefinition.SortOrder"/> 0, 1, …). Each
    /// call gives each station a new random id (card R2a: a fixed id would let anyone compute the item id, see
    /// <see cref="StationIds"/>); the seed is saved, so a starter keeps its id, its item and its favourites across restarts.
    /// </summary>
    /// <returns>The stations; each call returns new instances.</returns>
    public static IReadOnlyList<StationDefinition> Create()
    {
        var stations = new List<StationDefinition>
        {
            new()
            {
                Id = StationIds.NewId(),
                Name = "SUB/WAVE demo",
                Kind = StationKind.SubWave,
                BaseUrl = "https://www.getsubwave.com",
                StreamPath = StationDefinition.DefaultStreamPath,
                Codec = "mp3",
                Container = "mp3",
                BitrateKbps = 192,
                SampleRate = 44100,
                Channels = 2
            },
            new()
            {
                Id = StationIds.NewId(),
                Name = "KEXP",
                Kind = StationKind.Stream,
                StreamUrl = "https://kexp.streamguys1.com/kexp160.aac",
                LogoUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/d/dd/KEXP_logo_%28black_on_orange%29.svg/500px-KEXP_logo_%28black_on_orange%29.svg.png",
                Website = "https://www.kexp.org/",
                Codec = "aac",
                Container = "aac",
                BitrateKbps = 160,
                SampleRate = 44100,
                Channels = 2
            },
            new()
            {
                Id = StationIds.NewId(),
                Name = "WRUW 91.1 FM",
                Kind = StationKind.Stream,
                StreamUrl = "http://wruw-stream.wruw.org:8000/stream128.mp3",
                LogoUrl = "https://wruw.org/wp-content/uploads/fbrfg/apple-touch-icon.png",
                Website = "https://wruw.org/",
                Codec = "mp3",
                Container = "mp3",
                BitrateKbps = 128,
                SampleRate = 48000,
                Channels = 2
            },
            new()
            {
                Id = StationIds.NewId(),
                Name = "WJCU 88.7 FM",
                Kind = StationKind.Stream,
                StreamUrl = "https://wjcu.jcu.edu/listen/wjcu_radio/wjcu-mp3-hi",
                LogoUrl = "https://www.wjcu.org/files/2020/11/site-icon.png",
                Website = "https://www.wjcu.org/",
                Codec = "mp3",
                Container = "mp3",
                BitrateKbps = 128,
                SampleRate = 44100,
                Channels = 2
            },
            new()
            {
                Id = StationIds.NewId(),
                Name = "XCSB",
                Kind = StationKind.Stream,
                StreamUrl = "https://xcsb.live/listen/xcsb-cleveland/xcsb160.mp3",
                LogoUrl = "https://xcsb.live/static/uploads/browser_icon/original.png",
                Website = "https://xcsb.live/",
                Genre = "Freeform",
                Notes = "Cleveland's Alternative to FM.",
                Codec = "mp3",
                Container = "mp3",
                BitrateKbps = 160,
                SampleRate = 44100,
                Channels = 2
            }
        };

        for (var i = 0; i < stations.Count; i++)
        {
            stations[i].SortOrder = i;
        }

        return stations;
    }

    /// <summary>
    /// Adds the starter stations to a configuration whose station list is empty. <c>Plugin</c> calls it only for a fresh
    /// install (no configuration file); the empty-list check is a second guard, so the starters can never land twice or
    /// next to a station the admin has.
    /// </summary>
    /// <param name="config">The configuration, changed in place.</param>
    /// <returns>True when the starters were added.</returns>
    public static bool Seed(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Stations ??= new Collection<StationDefinition>();
        if (config.Stations.Count > 0)
        {
            return false;
        }

        foreach (var station in Create())
        {
            config.Stations.Add(station);
        }

        return true;
    }
}
