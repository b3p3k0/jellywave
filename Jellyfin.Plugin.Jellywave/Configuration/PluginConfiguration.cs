using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// Plugin configuration. The 0.4.x <c>Delivery</c> and <c>ClientStreamUrl</c> fields are gone since card L10 (every
/// station is relayed): <c>XmlSerializer</c> skips them in an older file, and the next save drops them.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Smallest allowed now-playing poll interval, in seconds.
    /// </summary>
    public const int MinPollSeconds = 5;

    /// <summary>
    /// Largest allowed now-playing poll interval, in seconds.
    /// </summary>
    public const int MaxPollSeconds = 300;

    /// <summary>
    /// Default stream bitrate in kbit/s when the station has not been queried (SUB/WAVE's default MP3 mount).
    /// </summary>
    public const int DefaultStreamBitrateKbps = 192;

    /// <summary>
    /// Default stream sample rate in Hz.
    /// </summary>
    public const int DefaultStreamSampleRate = 44100;

    /// <summary>
    /// Default stream channel count.
    /// </summary>
    public const int DefaultStreamChannels = 2;

    /// <summary>
    /// Default name of the My Media tile, on fresh installs and after the Live Radio migration alike.
    /// </summary>
    public const string DefaultLibraryName = "Live Radio";

    /// <summary>
    /// The configuration shape this build writes. 0 is the flat single-station shape of 0.4.x; 1 adds
    /// <see cref="Stations"/>, <see cref="LibraryName"/> and <see cref="PreviousChannelNames"/> (Live Radio, card L1).
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private const string CollectionSetterJustification
        = "Jellyfin saves the configuration page with System.Text.Json, which does not fill a get-only collection; XmlSerializer reads it either way.";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        StationBaseUrl = string.Empty;
        StreamPath = "/stream.mp3";
        StationName = "SUB/WAVE";
        StationTagline = string.Empty;
        NowPlayingEnabled = true;
        CoverArtEnabled = true;
        NowPlayingPollSeconds = 20;
        StreamBitrateKbps = DefaultStreamBitrateKbps;
        StreamSampleRate = DefaultStreamSampleRate;
        StreamChannels = DefaultStreamChannels;
        LastChangedUtc = DateTime.UnixEpoch;
        LibraryName = DefaultLibraryName;
        Stations = new Collection<StationDefinition>();
        PreviousChannelNames = new Collection<string>();
        CarriedChannelNames = new Collection<string>();
        SchemaVersion = 0;
    }

    /// <summary>
    /// Gets or sets the name shown on the My Media tile. Jellyfin derives the channel id from it, so renaming creates
    /// a new channel; the outgoing name is kept in <see cref="PreviousChannelNames"/> (the rename bridge).
    /// </summary>
    public string LibraryName { get; set; }

    /// <summary>
    /// Gets or sets the stations in the tile. Every enabled station that passes its rules is listed as an item (card L2).
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = CollectionSetterJustification)]
    public Collection<StationDefinition> Stations { get; set; }

    /// <summary>
    /// Gets or sets earlier tile names. Names, not ids: the ids are derived when used, through Jellyfin's own
    /// <c>GetNewItemId</c>, so they follow the server's <c>EnableCaseSensitiveItemIds</c> setting. A user whose block
    /// list names one of these ids is kept away from the tile and the station routes (the rename bridge).
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = CollectionSetterJustification)]
    public Collection<string> PreviousChannelNames { get; set; }

    /// <summary>
    /// Gets or sets the earlier tile names "Give them access to the renamed tile" has already run for. The pending count and the rename notice
    /// skip them, so an admin who later removes the tile from a user is never offered the same carry-over again. Blocks on
    /// these names are still honoured (<see cref="PreviousChannelNames"/>). Kept on every save like the earlier names.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = CollectionSetterJustification)]
    public Collection<string> CarriedChannelNames { get; set; }

    /// <summary>
    /// Gets or sets the configuration shape the file was written in; 0 for a 0.4.x file (see <see cref="CurrentSchemaVersion"/>).
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the SUB/WAVE base URL. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public string StationBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the stream mount path. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public string StreamPath { get; set; }

    /// <summary>
    /// Gets or sets the 0.4.x tile name, which was also the station name. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public string StationName { get; set; }

    /// <summary>
    /// Gets or sets the station tagline. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public string StationTagline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the now-playing poller is enabled.
    /// </summary>
    public bool NowPlayingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the poller saves the current track's cover art as the live item's image.
    /// Off, or when the station has no art for a track, the item shows the station logo.
    /// </summary>
    public bool CoverArtEnabled { get; set; }

    /// <summary>
    /// Gets or sets the now-playing poll interval in seconds. Read through <see cref="EffectivePollSeconds"/>.
    /// </summary>
    public int NowPlayingPollSeconds { get; set; }

    /// <summary>
    /// Gets or sets the stream bitrate in kbit/s. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public int StreamBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the stream sample rate in Hz. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public int StreamSampleRate { get; set; }

    /// <summary>
    /// Gets or sets the stream channel count. Legacy (0.4.x): read only by <see cref="ConfigMigration"/> and kept for a downgrade; the station lives in <see cref="Stations"/>.
    /// </summary>
    public int StreamChannels { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the configuration last changed. Drives channel cache invalidation.
    /// Defaults to the Unix epoch so an unsaved configuration is stable across restarts.
    /// </summary>
    public DateTime LastChangedUtc { get; set; }

    /// <summary>
    /// Gets the poll interval clamped to the allowed range.
    /// </summary>
    public int EffectivePollSeconds => Math.Clamp(NowPlayingPollSeconds, MinPollSeconds, MaxPollSeconds);

    /// <summary>
    /// The SUB/WAVE station the tile's description and a station route called without a station id serve (card L5; the
    /// poller polls every listed SUB/WAVE station since card L4, the routes serve each one by id): the
    /// <see cref="PrimaryCandidate"/>, and only while the tile lists it (<see cref="StationSources.Publish"/>). A candidate
    /// that fails its rules (a refused URL or format, an id another station already took) is no primary at all, so nothing
    /// is served or written for a station that is not published, and its id never reaches another station's item.
    /// </summary>
    /// <returns>The station, or null when there is none or the candidate is not listed.</returns>
    public StationDefinition? PrimaryStation() => StationSources.Primary(this)?.Station;

    /// <summary>
    /// The station that would be primary: the first enabled <see cref="StationKind.SubWave"/> station by
    /// <see cref="StationDefinition.SortOrder"/> (list order breaks ties), whether or not it passes its rules. Only the
    /// configuration page's status reads it, to say why nothing is served, and <see cref="StationSources.Primary"/> starts
    /// from it; everything else uses <see cref="PrimaryStation"/>.
    /// </summary>
    /// <returns>The station, or null when there is none.</returns>
    public StationDefinition? PrimaryCandidate()
        => (Stations ?? [])
            .Where(s => s is not null && s.Enabled && s.Kind == StationKind.SubWave)
            .OrderBy(s => s.SortOrder)
            .FirstOrDefault();
}
