using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave;

/// <summary>
/// Jellywave: surfaces internet radio stations (SUB/WAVE first) as a Jellyfin channel (a "My Media" tile).
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Why a save is refused while an unreadable settings file could not be copied aside (card R1). Thrown by every save
    /// and sent by <c>GET /Jellywave/Health</c>, where the page reads it (Jellyfin does not pass an exception's text on).
    /// </summary>
    internal const string SaveRefusedReason = "the settings file could not be read at startup and no copy of it could be kept, so Jellywave does not write over it; check that Jellyfin can read the settings file and write to its folder (see the server log)";

    /// <summary>The least a save moves <see cref="PluginConfiguration.LastChangedUtc"/> on, so the item date always advances.</summary>
    private static readonly TimeSpan MinChangeStep = TimeSpan.FromSeconds(1);

    private readonly Func<string, Guid> _channelId;
    private readonly ILogger<Plugin> _logger;
    private readonly ConfigurationFiles _files;
    private readonly TimeProvider _time;
    // Lock order: AccessGranter's SemaphoreSlim gate, then _saveGate (a carry-over records its names while holding the
    // granter gate). Nothing done under _saveGate may wait on the granter gate.
    private readonly Lock _saveGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class. Jellyfin 12.3 builds plugins in
    /// <c>ApplicationHost.FindParts</c> through <c>ActivatorUtilities</c>, after <c>SetStaticProperties</c> has already
    /// resolved <see cref="ILibraryManager"/>, so taking it here changes no startup order. The configuration is loaded and,
    /// when it is a 0.4.x file, migrated and saved here, before <see cref="Instance"/> is published, so no service ever
    /// reads the flat shape. With no configuration file at all (a fresh install) the plugin writes the first one itself,
    /// with the <see cref="StarterStations"/> (card L9). Any failure while loading or migrating is logged at Error and the
    /// plugin still starts with the configuration unmigrated, in memory (defaults when it could not be loaded at all), so
    /// it is never left Malfunctioned.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="libraryManager">The library manager, for Jellyfin's channel-id rule.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<Plugin> logger)
        : this(applicationPaths, xmlSerializer, libraryManager, logger, new ConfigurationFiles(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class with explicit file operations and clock (tests).
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="libraryManager">The library manager, for Jellyfin's channel-id rule.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="files">The file operations of the settings writer.</param>
    /// <param name="time">The clock for the change time and the name of an unreadable file's copy.</param>
    internal Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<Plugin> logger, ConfigurationFiles files, TimeProvider time)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        _logger = logger;
        _files = files;
        _time = time;
        _channelId = name => ChannelIds.Derive(libraryManager, name);

        // Before the first read of Configuration: the base class's lazy getter runs its LoadConfiguration, which writes
        // a default file when there is none (a fresh install could no longer be told from an upgrade) and writes defaults
        // over a file it cannot read (card R1). Both paths below assign Configuration themselves, so it never runs.
        if (File.Exists(ConfigurationFilePath))
        {
            LoadExisting();
        }
        else
        {
            StartFresh();
        }

        RekeyPublicIds();
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Jellywave";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6ca437a0-5b11-41c6-8fe7-92e5de66e2fe");

    /// <summary>
    /// Gets a value indicating whether the configuration file could not be loaded at startup, so the plugin runs on
    /// defaults in memory. It cannot know the real 0.4.x tile name then, so blocks on it do not hold until the file is
    /// readable again. Nothing is saved on its own while it is set; the next successful save from the configuration page
    /// clears it.
    /// </summary>
    public bool RunningOnDefaults { get; private set; }

    /// <summary>
    /// Gets the file name (no folder) of the copy the unreadable configuration file was kept as (card R1), while
    /// <see cref="RunningOnDefaults"/>; null when there is no such copy (yet). Set only after the copy was written.
    /// </summary>
    public string? UnreadableCopy { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the unreadable configuration file could not be copied aside (card R1). Every save
    /// is refused while it is set (<see cref="SaveRefusedReason"/>), so the file is never overwritten without a copy, and
    /// every save tries the copy again first; no restart is needed once the folder lets it through.
    /// </summary>
    public bool PreservationFailed { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the first configuration of a fresh install (card L9) could not be saved, so the
    /// plugin runs on the starter stations in memory. There was no file and no earlier tile, so nothing is lost but the
    /// starters themselves; the folder refused the write. Cleared by the next successful save from the configuration page.
    /// </summary>
    public bool RunningUnsaved { get; private set; }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Writes a configuration to the settings file, for every save (page saves, carried names, the migration, the fresh
    /// install, and any call from Jellyfin): through <see cref="ConfigurationFile.Write"/>, so the file is always a whole
    /// one, old or new. While <see cref="PreservationFailed"/> it first tries to copy the unreadable file aside again and,
    /// when that fails, refuses with <see cref="SaveRefusedReason"/>. It does not change <see cref="BasePlugin{T}.Configuration"/>.
    /// It has no <see cref="RunningOnDefaults"/> guard of its own, because the page save that ends that state goes
    /// through it: every other caller in the plugin must not save while <see cref="RunningOnDefaults"/> is set (the
    /// migration and the fresh install never run then, and <see cref="RecordCarriedNames"/> checks it).
    /// </summary>
    /// <param name="config">The configuration to write.</param>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_saveGate)
        {
            if (PreservationFailed && !TryKeepUnreadable())
            {
                throw new InvalidOperationException(SaveRefusedReason);
            }

            // Serialized whole before any file is touched: a serializer failure leaves no new file behind. On defaults the
            // file replaced is the unreadable one, already kept as its copy, so no new .previous is made and the one there
            // (usually the last file this plugin wrote) survives.
            using var buffer = new MemoryStream();
            XmlSerializer.SerializeToStream(config, buffer);
            ConfigurationFile.Write(_files, ConfigurationFilePath, buffer.ToArray(), keepPrevious: !RunningOnDefaults);
        }
    }

    /// <summary>
    /// Saves a configuration posted by the configuration page (Jellyfin 12.3 <c>PluginsController.UpdatePluginConfiguration</c>
    /// calls this with the deserialized body). A copy of the body is prepared (the rename bridge records the outgoing tile
    /// name and keeps every earlier one, <see cref="ConfigMigration.PrepareSave"/>, server side, so no client can drop
    /// them), stamped with the server's change time and written; only then does it take effect (card R1: the base class
    /// would assign first, so a failed write would leave memory ahead of the file). Card R2a: a station posted with a
    /// publicly known id (<see cref="StationIds.KnownPublicIds"/>) gets a random one before the write. A failed write throws
    /// and changes nothing. The base class's <c>UpdateConfiguration</c> is never called.
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not PluginConfiguration incoming)
        {
            throw new ArgumentException("Not a Jellywave configuration", nameof(configuration));
        }

        PluginConfiguration candidate;

        // One gate with RecordCarriedNames, so a carry-over recorded during a page save is merged, not lost.
        lock (_saveGate)
        {
            var current = Configuration;
            candidate = ConfigurationFile.Copy(incoming);
            ConfigMigration.PrepareSave(current, candidate, _channelId);

            // Card R2a fix round: a page opened before the upgrade restart still holds the public ids; they never go back
            // to disk.
            StationIds.Rekey(candidate);

            // The client's value is ignored: a browser clock behind the last save would move the item date back, and
            // Jellyfin re-saves an item's media sources only when that date advances.
            candidate.LastChangedUtc = NextChangeTime(current, _time.GetUtcNow().UtcDateTime);
            SaveConfiguration(candidate);

            Configuration = candidate;
            RunningOnDefaults = false;
            RunningUnsaved = false;
            UnreadableCopy = null;
        }

        // Outside the gate: a handler may wait on anything. Each one runs, and none can undo a save that is on disk.
        if (ConfigurationChanged is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<BasePluginConfiguration>)handler)(this, candidate);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A handler of the configuration change failed; the configuration is saved and in effect");
                }
            }
        }
    }

    /// <summary>
    /// Records the earlier tile names "Give them access to the renamed tile" ran for (<see cref="PluginConfiguration.CarriedChannelNames"/>)
    /// on a copy of the configuration in effect, writes it, and only then puts it in effect (card R1). A failed write is
    /// logged and changes nothing. While the configuration is in memory only, the names wait there for the next page save.
    /// </summary>
    /// <param name="names">The earlier tile names.</param>
    public void RecordCarriedNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_saveGate)
        {
            var candidate = ConfigurationFile.Copy(Configuration);
            var changed = false;
            foreach (var name in names)
            {
                changed |= ConfigMigration.AddCarriedName(candidate, name, _channelId);
            }

            if (!changed)
            {
                return;
            }

            if (RunningOnDefaults || RunningUnsaved)
            {
                // The file on disk could not be read; saving these in-memory defaults would replace it before the admin
                // has looked at it. After a fresh install's failed first save there is nothing to overwrite, but only a
                // page save clears the warning, so the names wait for it the same way. A missing copy of the unreadable
                // file is tried again here too, like on every save.
                if (PreservationFailed && !TryKeepUnreadable())
                {
                    _logger.LogWarning("Carried tile names not saved: {Reason}; kept in memory until a save from Dashboard > Plugins > Jellywave works", SaveRefusedReason);
                }
                else
                {
                    _logger.LogWarning("The configuration is in memory only (it could not be {Failed} at startup); carried tile names kept in memory until the next save from Dashboard > Plugins > Jellywave", RunningOnDefaults ? "loaded" : "saved");
                }

                Configuration = candidate;
                return;
            }

            try
            {
                SaveConfiguration(candidate);
                Configuration = candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not save which tile names access was carried over from; they are not recorded, so the carry-over may be offered again");
            }
        }
    }

    /// <summary>
    /// The change time a save stamps (card R1): now on the server, but always at least <see cref="MinChangeStep"/> after
    /// the item date the previous configuration gave (its <see cref="PluginConfiguration.LastChangedUtc"/>, or
    /// <see cref="JellywaveChannel.MediaSourceSchemaDate"/> when later), so every save moves the items' <c>DateModified</c>
    /// on, whatever either clock says.
    /// </summary>
    /// <param name="previous">The configuration in effect before the save.</param>
    /// <param name="utcNow">The server's current UTC time.</param>
    /// <returns>The new <see cref="PluginConfiguration.LastChangedUtc"/>, in UTC.</returns>
    internal static DateTime NextChangeTime(PluginConfiguration previous, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var last = previous.LastChangedUtc.Kind switch
        {
            DateTimeKind.Local => previous.LastChangedUtc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(previous.LastChangedUtc, DateTimeKind.Utc),
            _ => previous.LastChangedUtc
        };
        var effective = last > JellywaveChannel.MediaSourceSchemaDate ? last : JellywaveChannel.MediaSourceSchemaDate;
        var floor = effective < DateTime.MaxValue - MinChangeStep ? effective + MinChangeStep : effective;
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return now > floor ? now : floor;
    }

    /// <summary>
    /// Card R2a (HI decision B): gives every station whose id is the same on every install (<see cref="StationIds.KnownPublicIds"/>:
    /// the 0.4.x migration's <c>live</c>, the starters' ids before 0.5.0.0) a new random id, once. Runs in the constructor
    /// after the load, the migration or the fresh install, before <see cref="Instance"/> is published. Like every change, it
    /// is written before it takes effect (card R1): a copy is re-keyed, stamped and saved, then put in effect. While the
    /// plugin may not save on its own (<see cref="RunningOnDefaults"/>, <see cref="RunningUnsaved"/>) the copy is put in
    /// effect in memory only, and the next page save writes it. A failed save is logged and changes nothing; the next start
    /// tries again. The new ids make new items: favourites and play state on the old items are not carried (accepted).
    /// </summary>
    private void RekeyPublicIds()
    {
        try
        {
            var candidate = ConfigurationFile.Copy(Configuration);
            if (!StationIds.Rekey(candidate))
            {
                return;
            }

            candidate.LastChangedUtc = NextChangeTime(Configuration, _time.GetUtcNow().UtcDateTime);
            if (RunningOnDefaults || RunningUnsaved)
            {
                Configuration = candidate;
                _logger.LogWarning("Gave the stations with a publicly known id new random ids in memory; they are saved with the next save from Dashboard > Plugins > Jellywave");
                return;
            }

            SaveConfiguration(candidate);
            Configuration = candidate;
            _logger.LogInformation("Gave the stations with a publicly known id new random ids, so their item ids cannot be guessed; their favourites and play state start afresh");
        }
        catch (Exception ex)
        {
            // Any failure: the constructor must not throw (Malfunctioned), and memory still matches the file.
            _logger.LogWarning(ex, "Could not give the stations with a publicly known id new ids; they keep their ids until the next start tries again");
        }
    }

    /// <summary>
    /// Loads an existing settings file with the plugin's own serializer and assigns it, so the base class's
    /// <c>LoadConfiguration</c> (which writes defaults over a file it cannot read) never runs. A file that cannot be read
    /// is copied aside and the plugin runs on defaults in memory, saving nothing (card R1); a readable one is migrated.
    /// </summary>
    private void LoadExisting()
    {
        PluginConfiguration? loaded = null;
        Exception? failure = null;
        try
        {
            loaded = XmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath) as PluginConfiguration;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (loaded is not null)
        {
            Configuration = loaded;
            MigrateLoadedConfiguration(loaded);
            return;
        }

        Configuration = new PluginConfiguration();
        RunningOnDefaults = true;
        _logger.LogError(failure, "Could not read the configuration file; Jellywave runs on defaults in memory and writes nothing until it is saved from Dashboard > Plugins > Jellywave");
        TryKeepUnreadable();
    }

    /// <summary>
    /// Copies the unreadable settings file aside (<see cref="ConfigurationFile.KeepUnreadable"/>) and records the copy's
    /// name, or sets <see cref="PreservationFailed"/> when the copy fails. A file that is gone by now needs no copy.
    /// </summary>
    /// <returns>True when nothing on disk is left to lose: the copy is written, or the file is gone.</returns>
    private bool TryKeepUnreadable()
    {
        try
        {
            if (!File.Exists(ConfigurationFilePath))
            {
                PreservationFailed = false;
                return true;
            }

            UnreadableCopy = ConfigurationFile.KeepUnreadable(_files, ConfigurationFilePath, _time.GetUtcNow().UtcDateTime);
            PreservationFailed = false;
            _logger.LogWarning("Kept the unreadable configuration file as {Copy}, beside it; saving Dashboard > Plugins > Jellywave writes a new one", UnreadableCopy);
            return true;
        }
        catch (Exception ex)
        {
            // Any failure counts: the constructor must not throw, and no save may run without the copy.
            PreservationFailed = true;
            _logger.LogError(ex, "Could not copy the unreadable configuration file aside; every save is refused until a copy works, so the file is never overwritten");
            return false;
        }
    }

    /// <summary>
    /// Writes the first configuration of a fresh install (card L9): the defaults, through the same migration a default
    /// file would get (schema 1, the tile "Live Radio"), plus the <see cref="StarterStations"/>. This replaces the default
    /// file Jellyfin's <c>LoadConfiguration</c> would have written, so a file on disk always means "not fresh", and a
    /// station the admin removes, or a list the admin empties, is saved as such and never put back. A failed save keeps
    /// the starters in memory and reports <see cref="RunningUnsaved"/>; the writer leaves no file then (card R1), so the
    /// next start is a fresh install again.
    /// </summary>
    private void StartFresh()
    {
        var config = new PluginConfiguration();
        try
        {
            ConfigMigration.Migrate(config, _channelId);
        }
        catch (Exception ex)
        {
            // Migrate derives every id before it changes anything; the next start migrates the saved file again.
            _logger.LogError(ex, "Could not migrate the new configuration to Live Radio; it is saved unmigrated and the migration is tried again at the next start");
        }

        StarterStations.Seed(config);
        Configuration = config;
        try
        {
            SaveConfiguration(config);
            _logger.LogInformation("New configuration with {Stations} starter station(s); remove any of them in Dashboard > Plugins > Jellywave", config.Stations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the new configuration; Jellywave runs on the starter stations in memory until it is saved from Dashboard > Plugins > Jellywave");
            RunningUnsaved = true;
        }
    }

    /// <summary>
    /// Migrates a loaded 0.4.x shape and saves it. Runs in the constructor, before <see cref="Instance"/> is published,
    /// so nothing reads the configuration while it changes. A failed save keeps the migrated copy in memory; the next start
    /// migrates again from the legacy fields, which are never cleared.
    /// </summary>
    /// <param name="config">The configuration as loaded, already in effect.</param>
    private void MigrateLoadedConfiguration(PluginConfiguration config)
    {
        var legacyTile = ConfigMigration.LegacyTileName(config);
        bool migrated;
        try
        {
            migrated = ConfigMigration.Migrate(config, _channelId);
        }
        catch (Exception ex)
        {
            // Migrate derives every id before it changes anything, so the configuration is still exactly as loaded.
            _logger.LogError(ex, "Could not migrate the configuration to Live Radio; it runs unmigrated in memory and the migration is tried again at the next start");
            return;
        }

        if (!migrated)
        {
            return;
        }

        _logger.LogInformation(
            "Configuration moved to Live Radio (schema {Schema}): {Stations} station(s); the tile {Old} is now {New}. Users who had the old tile by name see the new one after Dashboard > Plugins > Jellywave > Give them access to the renamed tile",
            config.SchemaVersion,
            config.Stations.Count,
            legacyTile,
            config.LibraryName);
        try
        {
            SaveConfiguration(config);
        }
        catch (Exception ex)
        {
            // Any failure: the constructor must not throw (Malfunctioned), and the file is still the old one.
            _logger.LogWarning(ex, "Could not save the migrated configuration; it is used from memory and migrated again at the next start");
        }
    }
}
