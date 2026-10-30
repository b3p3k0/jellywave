using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R1: the settings file is written whole or not at all, a save takes effect only once it is on disk, an unreadable
/// file is copied aside and never written over, and the server stamps the change time. A real <see cref="Plugin"/> on a
/// temporary folder with a real XML serializer; the writer's file steps fail on demand (<see cref="FaultyFiles"/>).
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class SettingsSafetyTests : IDisposable
{
    // Cut off in the middle of an element: what a crash during a 0.4.x save (Jellyfin's SerializeToFile) leaves behind.
    private const string Truncated = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <LibraryName>Kevin Radio</LibraryName>
          <Stations>
            <StationDefinition>
              <Id>keep-me</Id>
              <Na
        """;

    private static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 34, 56, 789, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellywave-r1-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibrary _library = FakeLibrary.Create();
    private readonly FileXmlSerializer _xml = new();
    private readonly FaultyFiles _files = new();
    private readonly ManualClock _clock = new();

    public SettingsSafetyTests()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    }

    /// <summary>Where a save fails.</summary>
    public enum FailurePoint
    {
        /// <summary>The new file cannot be created.</summary>
        BeforeWriting,

        /// <summary>Some bytes are written, then the write fails.</summary>
        AfterSomeBytes,

        /// <summary>The swap fails before it changes anything.</summary>
        SwapBefore,

        /// <summary>The swap happens, then reports a failure.</summary>
        SwapAfter,

        /// <summary>
        /// The swap moves the old file to the backup name, then fails with the new file still under its temp name (Windows
        /// <c>ReplaceFile</c>, ERROR_UNABLE_TO_MOVE_REPLACEMENT_2).
        /// </summary>
        SwapHalfway
    }

    private string ConfigDir => Path.Combine(_root, "configurations");

    private string ConfigFile => Path.Combine(ConfigDir, Path.ChangeExtension(Path.GetFileName(typeof(Plugin).Assembly.Location), ".xml"));

    public void Dispose()
    {
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);
        Directory.Delete(_root, true);
    }

    /// <summary>Real file operations, each of which a test can make fail.</summary>
    internal sealed class FaultyFiles : ConfigurationFiles
    {
        public FailurePoint? Fail { get; set; }

        public bool FailCopy { get; set; }

        public bool FailTempMove { get; set; }

        public string? LastBackup { get; private set; } = "none";

        public int CopyAttempts { get; private set; }

        public override FileStream CreateNew(string path)
        {
            if (Fail == FailurePoint.BeforeWriting && path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                throw new IOException("disk full before writing");
            }

            return base.CreateNew(path);
        }

        public override void Write(FileStream file, byte[] bytes)
        {
            if (Fail == FailurePoint.AfterSomeBytes)
            {
                file.Write(bytes, 0, bytes.Length / 3);
                file.Flush(true);
                throw new IOException("disk full after some bytes");
            }

            base.Write(file, bytes);
        }

        public override void Replace(string source, string target, string? backup)
        {
            LastBackup = backup;
            if (Fail == FailurePoint.SwapHalfway)
            {
                File.Move(target, backup ?? target + ".gone", true);
                throw new IOException("unable to move the replacement file to the replaced file");
            }

            if (Fail == FailurePoint.SwapBefore)
            {
                throw new IOException("swap failed");
            }

            base.Replace(source, target, backup);
            if (Fail == FailurePoint.SwapAfter)
            {
                throw new IOException("swap reported a failure after it happened");
            }
        }

        public override void Move(string source, string target)
        {
            if (FailTempMove && source.EndsWith(".tmp", StringComparison.Ordinal))
            {
                throw new IOException("rename failed");
            }

            if (Fail is FailurePoint.SwapBefore)
            {
                throw new IOException("rename failed");
            }

            base.Move(source, target);
        }

        public override void Copy(FileStream source, FileStream target)
        {
            CopyAttempts++;
            if (FailCopy)
            {
                // After the copy's file was created: the half copy must not stay behind.
                target.WriteByte((byte)'<');
                throw new IOException("copy failed");
            }

            base.Copy(source, target);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Noon;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Plugin NewPlugin()
    {
        var paths = (PluginWiringTests.Paths)(object)DispatchProxy.Create<IApplicationPaths, PluginWiringTests.Paths>();
        paths.Root = _root;
        return new Plugin((IApplicationPaths)(object)paths, _xml, _library.Manager, NullLogger<Plugin>.Instance, _files, _clock);
    }

    private static PluginConfiguration Named(string tile, string stationId)
        => new()
        {
            SchemaVersion = 1,
            LibraryName = tile,
            Stations = { new StationDefinition { Id = stationId, Name = stationId, Kind = StationKind.Stream, StreamUrl = "https://cdn.example/" + stationId + ".mp3" } }
        };

    // A plugin whose file holds tile "A" (station "a"), saved once from the page.
    private Plugin SavedA()
    {
        var plugin = NewPlugin();
        plugin.UpdateConfiguration(Named("A", "a"));
        Assert.Equal("A", OnDisk().LibraryName);
        return plugin;
    }

    private PluginConfiguration OnDisk() => (PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), ConfigFile);

    private string[] FilesInFolder() => Directory.GetFiles(ConfigDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private string[] UnreadableCopies() => FilesInFolder().Where(n => n.Contains(".unreadable-", StringComparison.Ordinal)).ToArray();

    private string[] TempFiles() => FilesInFolder().Where(n => n.EndsWith(".tmp", StringComparison.Ordinal)).ToArray();

    private static string FileName(string path) => Path.GetFileName(path);

    private Api.HealthResponse Health()
    {
        var station = new StationClient(null!);
        return new Api.JellywaveController(
            station,
            null!,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            new LiveItemFinder(_library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<Api.JellywaveController>.Instance,
            () => Plugin.Instance?.Configuration).Health().Value!;
    }

    private static DateTime ItemDate(Plugin plugin)
    {
        var channel = new JellywaveChannel(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), null!, new RenameBridge(_ => Guid.Empty), () => plugin.Configuration);
        var items = channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None).GetAwaiter().GetResult().Items;
        return Assert.Single(items).DateModified;
    }

    [Fact]
    public void A_save_that_fails_before_writing_keeps_the_old_memory_and_the_old_file()
    {
        var plugin = SavedA();
        var before = plugin.Configuration;
        var bytes = File.ReadAllBytes(ConfigFile);
        _files.Fail = FailurePoint.BeforeWriting;

        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(Named("B", "b")));

        Assert.Same(before, plugin.Configuration);
        Assert.Equal("A", plugin.Configuration.LibraryName);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigFile));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void A_save_that_fails_after_some_bytes_reports_it_and_leaves_memory_and_file_as_they_were()
    {
        var plugin = SavedA();
        var before = plugin.Configuration;
        var bytes = File.ReadAllBytes(ConfigFile);
        _files.Fail = FailurePoint.AfterSomeBytes;

        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(Named("B", "b")));

        Assert.Same(before, plugin.Configuration);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigFile));
        Assert.Equal("A", OnDisk().LibraryName);   // the old file loads
        Assert.Empty(TempFiles());                  // the partial new file is gone
    }

    [Theory]
    [InlineData(FailurePoint.SwapBefore, "A")]
    [InlineData(FailurePoint.SwapAfter, "B")]
    public void A_swap_that_fails_leaves_the_old_or_the_new_complete_file(FailurePoint point, string onDisk)
    {
        var plugin = SavedA();
        var before = plugin.Configuration;
        _files.Fail = point;

        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(Named("B", "b")));

        Assert.Same(before, plugin.Configuration);
        Assert.Equal(onDisk, OnDisk().LibraryName);
        Assert.Equal(onDisk == "A" ? "a" : "b", Assert.Single(OnDisk().Stations).Id);
        Assert.Empty(TempFiles());
    }

    [Theory]
    [InlineData(false, "B")]
    [InlineData(true, "A")]
    public void A_swap_that_stops_halfway_never_leaves_the_settings_file_missing(bool renameFails, string expected)
    {
        // Windows ReplaceFile can rename the old file to .previous and then fail to rename the new one into place. Without
        // a repair no settings file is left: the next start would seed the starters and the next save overwrite .previous.
        var plugin = SavedA();
        _files.Fail = FailurePoint.SwapHalfway;
        _files.FailTempMove = renameFails;

        if (renameFails)
        {
            // The new file cannot be put in place either: the old one is renamed back and the save fails.
            Assert.Throws<IOException>(() => plugin.UpdateConfiguration(Named("B", "b")));
            Assert.Equal("A", plugin.Configuration.LibraryName);
        }
        else
        {
            // The complete new file is renamed into place: the save is done, with the old file as .previous.
            plugin.UpdateConfiguration(Named("B", "b"));
            Assert.Equal("B", plugin.Configuration.LibraryName);
            Assert.Equal("A", ((PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), ConfigFile + ConfigurationFile.PreviousSuffix)).LibraryName);
        }

        Assert.True(File.Exists(ConfigFile));
        Assert.Equal(expected, OnDisk().LibraryName);
        Assert.Empty(TempFiles());
        _files.Fail = null;
        _files.FailTempMove = false;
        var restarted = NewPlugin();
        Assert.Equal(expected, restarted.Configuration.LibraryName);   // the admin's settings, not the starters
        Assert.Equal(expected.ToLowerInvariant(), Assert.Single(restarted.Configuration.Stations).Id);
        Assert.False(restarted.RunningOnDefaults || restarted.RunningUnsaved);
    }

    [Fact]
    public void The_first_save_on_defaults_keeps_the_last_good_previous_file()
    {
        // Two good saves: the file holds B, .previous holds A. Then the file goes bad.
        var plugin = SavedA();
        plugin.UpdateConfiguration(Named("B", "b"));
        var previous = ConfigFile + ConfigurationFile.PreviousSuffix;
        var lastGood = File.ReadAllBytes(previous);
        Assert.Equal("A", ((PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), previous)).LibraryName);
        File.WriteAllText(ConfigFile, Truncated);

        var restarted = NewPlugin();
        Assert.True(restarted.RunningOnDefaults);
        restarted.UpdateConfiguration(Named("C", "c"));

        Assert.Equal("C", OnDisk().LibraryName);
        Assert.Equal(lastGood, File.ReadAllBytes(previous));   // not replaced by the unreadable file
        Assert.Null(_files.LastBackup);
        Assert.Equal(Truncated, File.ReadAllText(Path.Combine(ConfigDir, Assert.Single(UnreadableCopies()))));

        // Off defaults again, saves keep a .previous as before.
        restarted.UpdateConfiguration(Named("D", "d"));
        Assert.Equal("C", ((PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), previous)).LibraryName);
    }

    [Fact]
    public void A_save_and_an_unreadable_copy_keep_the_files_unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // no Unix permissions to keep
        }

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var plugin = SavedA();
        File.SetUnixFileMode(ConfigFile, OwnerOnly);

        plugin.UpdateConfiguration(Named("B", "b"));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(ConfigFile));

        const UnixFileMode GroupRead = OwnerOnly | UnixFileMode.GroupRead;
        File.SetUnixFileMode(ConfigFile, GroupRead);
        plugin.UpdateConfiguration(Named("C", "c"));
        Assert.Equal(GroupRead, File.GetUnixFileMode(ConfigFile));

        // An unreadable file's copy is never readable by more users than the file was.
        File.WriteAllText(ConfigFile, Truncated);
        File.SetUnixFileMode(ConfigFile, OwnerOnly);
        NewPlugin();
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Combine(ConfigDir, Assert.Single(UnreadableCopies()))));
    }

    [Fact]
    public void A_first_save_that_fails_midway_leaves_no_file()
    {
        // No file yet (a fresh install's first save): the writer renames into place, so a failure leaves nothing, and the
        // next start is a fresh install again (L9 left a truncated file here before R1).
        _files.Fail = FailurePoint.AfterSomeBytes;

        var plugin = NewPlugin();

        Assert.True(plugin.RunningUnsaved);
        Assert.Empty(FilesInFolder());
        _files.Fail = null;
        Assert.False(NewPlugin().RunningUnsaved);
        Assert.Equal(StarterStations.Create().Select(s => s.Name), OnDisk().Stations.Select(s => s.Name));
    }

    [Theory]
    [InlineData(FailurePoint.BeforeWriting)]
    [InlineData(FailurePoint.AfterSomeBytes)]
    [InlineData(FailurePoint.SwapBefore)]
    [InlineData(FailurePoint.SwapAfter)]
    public void A_restart_after_any_failed_save_loads_a_complete_configuration(FailurePoint point)
    {
        var plugin = SavedA();
        _files.Fail = point;
        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(Named("B", "b")));
        _files.Fail = null;

        var restarted = NewPlugin();

        Assert.False(restarted.RunningOnDefaults);
        Assert.Empty(UnreadableCopies());
        Assert.Empty(TempFiles());
        var expected = point == FailurePoint.SwapAfter ? "B" : "A";   // the swap that happened reports late; still whole
        Assert.Equal(expected, restarted.Configuration.LibraryName);
        Assert.Equal(expected.ToLowerInvariant(), Assert.Single(restarted.Configuration.Stations).Id);
    }

    [Fact]
    public void Carried_names_whose_save_fails_are_not_applied()
    {
        var plugin = SavedA();
        var before = plugin.Configuration;
        var bytes = File.ReadAllBytes(ConfigFile);
        _files.Fail = FailurePoint.AfterSomeBytes;

        plugin.RecordCarriedNames(["Old"]);   // logged, not thrown: the granter's carry-over already ran

        Assert.Same(before, plugin.Configuration);
        Assert.Empty(before.CarriedChannelNames);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigFile));
        Assert.Empty(TempFiles());

        _files.Fail = null;
        plugin.RecordCarriedNames(["Old"]);
        Assert.Equal(new[] { "Old" }, plugin.Configuration.CarriedChannelNames);
        Assert.Equal(new[] { "Old" }, OnDisk().CarriedChannelNames);
        Assert.Empty(before.CarriedChannelNames);   // a new object took effect; the old one was never changed
    }

    [Fact]
    public void A_handler_that_throws_after_the_write_does_not_undo_the_save()
    {
        _xml.FailSave = true;
        var plugin = NewPlugin();   // a fresh install whose first save failed: "could not save" until a save works
        Assert.True(plugin.RunningUnsaved);
        _xml.FailSave = false;
        BasePluginConfiguration? seen = null;
        plugin.ConfigurationChanged += (_, _) => throw new InvalidOperationException("handler failed");
        plugin.ConfigurationChanged += (_, c) => seen = c;

        plugin.UpdateConfiguration(Named("B", "b"));   // does not throw: the page shows no "Not saved"

        Assert.Equal("B", plugin.Configuration.LibraryName);
        Assert.Equal("B", OnDisk().LibraryName);
        Assert.Same(plugin.Configuration, seen);   // the next handler still ran, with the configuration in effect
        Assert.False(plugin.RunningUnsaved);
        Assert.Null(Health().ConfigurationWarning);
    }

    [Fact]
    public void An_unreadable_file_in_a_writable_folder_is_copied_once_and_never_written()
    {
        File.WriteAllText(ConfigFile, Truncated);
        var original = File.ReadAllBytes(ConfigFile);

        var plugin = NewPlugin();

        Assert.True(plugin.RunningOnDefaults);
        Assert.False(plugin.PreservationFailed);
        Assert.Equal(original, File.ReadAllBytes(ConfigFile));
        Assert.Equal(0, _xml.Saves);
        var copy = Assert.Single(UnreadableCopies());
        Assert.Equal(FileName(ConfigFile) + ".unreadable-20261030T123456789Z.xml", copy);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigDir, copy)));
        Assert.Equal(new[] { FileName(ConfigFile), copy }.OrderBy(n => n, StringComparer.Ordinal), FilesInFolder());

        var health = Health();
        Assert.Equal(copy, health.ConfigurationUnreadableCopy);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, health.ConfigurationUnreadableCopy!);
        Assert.Equal(Api.JellywaveController.DefaultsWarning, health.ConfigurationWarning);
        Assert.Null(health.ConfigurationSaveRefusal);

        // Carried names wait in memory; nothing is written over the unreadable file.
        plugin.RecordCarriedNames(["SUB/WAVE"]);
        Assert.Equal(original, File.ReadAllBytes(ConfigFile));
        Assert.Equal(0, _xml.Saves);

        // The admin's save writes a new file; the copy stays as it was and the warning goes.
        plugin.UpdateConfiguration(Named("B", "b"));
        Assert.Equal("B", OnDisk().LibraryName);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigDir, copy)));
        Assert.False(File.Exists(ConfigFile + ConfigurationFile.PreviousSuffix));   // the copy keeps it; no new .previous
        Assert.Null(Health().ConfigurationWarning);
        Assert.Null(Health().ConfigurationUnreadableCopy);
    }

    [Fact]
    public void An_unreadable_file_whose_copy_fails_refuses_every_save_until_a_copy_works()
    {
        File.WriteAllText(ConfigFile, Truncated);
        var original = File.ReadAllBytes(ConfigFile);
        _files.FailCopy = true;

        var plugin = NewPlugin();
        var before = plugin.Configuration;

        Assert.True(plugin.RunningOnDefaults);
        Assert.True(plugin.PreservationFailed);
        Assert.Null(plugin.UnreadableCopy);
        Assert.Empty(UnreadableCopies());   // the half copy was removed
        var health = Health();
        Assert.Null(health.ConfigurationUnreadableCopy);
        Assert.Equal(Api.JellywaveController.PreservationWarning, health.ConfigurationWarning);
        Assert.Equal(Plugin.SaveRefusedReason, health.ConfigurationSaveRefusal);

        // Every save path refuses, and each one tries the copy again first.
        var attempts = _files.CopyAttempts;
        var refused = Assert.Throws<InvalidOperationException>(() => plugin.UpdateConfiguration(Named("B", "b")));
        Assert.Equal(Plugin.SaveRefusedReason, refused.Message);
        Assert.Equal(attempts + 1, _files.CopyAttempts);
        Assert.Throws<InvalidOperationException>(() => plugin.SaveConfiguration());
        Assert.Equal(attempts + 2, _files.CopyAttempts);
        plugin.RecordCarriedNames(["SUB/WAVE"]);
        Assert.Equal(attempts + 3, _files.CopyAttempts);
        Assert.Equal(original, File.ReadAllBytes(ConfigFile));
        Assert.Equal(0, _xml.Saves);
        Assert.Empty(UnreadableCopies());
        Assert.Equal("Live Radio", plugin.Configuration.LibraryName);
        Assert.NotSame(before, plugin.Configuration);   // the carried names replaced it in memory; never changed in place
        Assert.Empty(before.CarriedChannelNames);

        // The folder lets the copy through again: the next save copies first, then saves. No restart.
        _files.FailCopy = false;
        plugin.UpdateConfiguration(Named("B", "b"));

        Assert.False(plugin.PreservationFailed);
        Assert.False(plugin.RunningOnDefaults);
        var copy = Assert.Single(UnreadableCopies());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigDir, copy)));
        Assert.Equal("B", OnDisk().LibraryName);
        Assert.Equal(new[] { "SUB/WAVE" }, OnDisk().CarriedChannelNames);   // the names that waited are saved with it
        Assert.Null(Health().ConfigurationWarning);
        Assert.Null(Health().ConfigurationSaveRefusal);
    }

    [Fact]
    public void Two_unreadable_starts_make_two_copies_and_leave_the_first_unchanged()
    {
        File.WriteAllText(ConfigFile, Truncated);
        var first = File.ReadAllBytes(ConfigFile);
        NewPlugin();
        var firstCopy = Path.Combine(ConfigDir, Assert.Single(UnreadableCopies()));
        var firstTime = File.GetLastWriteTimeUtc(firstCopy);

        // Another unreadable file, and a start in the same millisecond (the clock does not move): the name gets "-2".
        File.WriteAllText(ConfigFile, Truncated + "<x");
        var second = File.ReadAllBytes(ConfigFile);
        var plugin = NewPlugin();

        var copies = UnreadableCopies();
        Assert.Equal(2, copies.Length);
        Assert.Equal(first, File.ReadAllBytes(firstCopy));
        Assert.Equal(firstTime, File.GetLastWriteTimeUtc(firstCopy));
        var secondCopy = FileName(ConfigFile) + ".unreadable-20261030T123456789Z-2.xml";
        Assert.Contains(secondCopy, copies);
        Assert.Equal(second, File.ReadAllBytes(Path.Combine(ConfigDir, secondCopy)));
        Assert.Equal(secondCopy, plugin.UnreadableCopy);
        Assert.Equal(second, File.ReadAllBytes(ConfigFile));
    }

    [Fact]
    public void A_browser_clock_behind_the_previous_save_still_moves_the_item_date_on()
    {
        // The previous save was stamped a day ahead of the server's clock now (a browser clock ahead, before R1).
        var ahead = Noon.UtcDateTime.AddDays(1);
        var start = Named("A", "a");
        start.LastChangedUtc = ahead;
        _xml.SerializeToFile(start, ConfigFile);
        var plugin = NewPlugin();
        var before = ItemDate(plugin);
        Assert.Equal(ahead, before.ToUniversalTime());

        // The browser posts a time a day behind; the server ignores it.
        var posted = Named("A", "a");
        posted.LastChangedUtc = Noon.UtcDateTime.AddDays(-1);
        plugin.UpdateConfiguration(posted);

        var after = ItemDate(plugin);
        Assert.True(after > before, after.ToString("O") + " should be after " + before.ToString("O"));
        Assert.Equal(ahead.AddSeconds(1), plugin.Configuration.LastChangedUtc);
        Assert.Equal(after, ItemDate(NewPlugin()).ToUniversalTime());   // and it survives a restart

        // With the clocks right again, a save is stamped with the server's time.
        _clock.Now = Noon.AddDays(2);
        plugin.UpdateConfiguration(posted);
        Assert.Equal(Noon.UtcDateTime.AddDays(2), plugin.Configuration.LastChangedUtc);
    }

    [Fact]
    public void A_server_clock_before_the_media_source_schema_date_still_moves_the_item_date_on()
    {
        // The item date is max(LastChangedUtc, MediaSourceSchemaDate): a stamp below the floor would not move it.
        _clock.Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var plugin = SavedA();
        var first = ItemDate(plugin);
        plugin.UpdateConfiguration(Named("A", "a"));
        var second = ItemDate(plugin);

        Assert.True(first > JellywaveChannel.MediaSourceSchemaDate);
        Assert.True(second > first);
    }

    [Fact]
    public void The_change_time_treats_a_local_or_unspecified_time_as_the_instant_it_is()
    {
        var utc = new DateTime(2027, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc.AddSeconds(1), Plugin.NextChangeTime(new PluginConfiguration { LastChangedUtc = utc.ToLocalTime() }, now));
        Assert.Equal(utc.AddSeconds(1), Plugin.NextChangeTime(new PluginConfiguration { LastChangedUtc = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) }, now));
        Assert.Equal(DateTimeKind.Utc, Plugin.NextChangeTime(new PluginConfiguration(), now).Kind);
        Assert.Equal(now, Plugin.NextChangeTime(new PluginConfiguration(), now));
        Assert.Equal(DateTime.MaxValue, Plugin.NextChangeTime(new PluginConfiguration { LastChangedUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) }, now));
    }

    [Fact]
    public void A_fresh_install_and_a_migration_write_through_the_same_writer_and_behave_as_before()
    {
        // Fresh install: one file with the starters, no previous copy, no new file left behind.
        var plugin = NewPlugin();
        Assert.Equal(StarterStations.Create().Select(s => s.Name), OnDisk().Stations.Select(s => s.Name));
        Assert.Equal(plugin.Configuration.Stations.Select(s => s.Id), OnDisk().Stations.Select(s => s.Id));
        Assert.Equal(new[] { FileName(ConfigFile) }, FilesInFolder());
        Assert.False(plugin.RunningOnDefaults || plugin.RunningUnsaved || plugin.PreservationFailed);

        // A 0.4.x file: migrated and saved, the 0.4.x file kept as the one previous copy.
        File.Delete(ConfigFile);
        const string Old = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <StationBaseUrl>http://radio.example:7700</StationBaseUrl>
              <StationName>IHateRadio</StationName>
            </PluginConfiguration>
            """;
        File.WriteAllText(ConfigFile, Old);
        var migrated = NewPlugin();

        Assert.Equal(1, migrated.Configuration.SchemaVersion);
        Assert.Equal(1, OnDisk().SchemaVersion);
        Assert.Equal(Assert.Single(migrated.Configuration.Stations).Id, Assert.Single(OnDisk().Stations).Id);
        Assert.Equal(Old, File.ReadAllText(ConfigFile + ConfigurationFile.PreviousSuffix));
        Assert.Empty(TempFiles());
        Assert.Empty(UnreadableCopies());
    }
}
