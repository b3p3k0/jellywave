using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// How the settings file is written and kept (card R1). A save never leaves a partial file: the bytes go to a new file
/// in the same folder, which is then swapped in as one step, so after a save, failed or not, the settings file is the old
/// complete one or the new complete one (<see cref="Write"/> says how a swap that fails halfway is put right). A file
/// that cannot be read is copied aside before anything could write over it.
/// </summary>
internal static class ConfigurationFile
{
    /// <summary>Suffix of the one previous copy each save keeps beside the settings file.</summary>
    internal const string PreviousSuffix = ".previous";

    /// <summary>Marks a copy of a settings file that could not be read: <c>&lt;file&gt;.unreadable-&lt;UTC time&gt;.xml</c>.</summary>
    internal const string UnreadableInfix = ".unreadable-";

    /// <summary>Suffix of the file a save writes before swapping it in.</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>The UTC time in an unreadable copy's name, to the millisecond.</summary>
    private const string CopyTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>
    /// How many copies one millisecond may get (<c>-2</c>, <c>-3</c>, …) before the copy fails; far above two starts in
    /// the same millisecond, and it bounds the loop.
    /// </summary>
    private const int MaxCopiesPerTime = 100;

    private static readonly XmlSerializer CopySerializer = new(typeof(PluginConfiguration));

    /// <summary>
    /// Writes the settings file: the bytes to a new uniquely named file in the same folder (with the old file's Unix
    /// permissions, flushed to disk and closed), then swapped in with <see cref="ConfigurationFiles.Replace"/> (keeping
    /// the old file as <c>&lt;file&gt;.previous</c> when <paramref name="keepPrevious"/>) or renamed into place when there
    /// is no file yet. A failure before the swap deletes the new file (best effort) and throws; the settings file is then
    /// still the old one. A swap that fails after it moved the old file away (Windows <c>ReplaceFile</c> can stop after
    /// renaming the old file to the backup name) is finished instead: the complete new file is renamed into place and the
    /// save counts as done, or, when that fails too, the backup is renamed back and the failure thrown. The settings file
    /// is never left missing while a complete new file or the backup exists.
    /// </summary>
    /// <param name="files">The file operations.</param>
    /// <param name="target">The settings file.</param>
    /// <param name="bytes">The whole new content.</param>
    /// <param name="keepPrevious">False to swap without a new <c>.previous</c>, leaving the one there untouched.</param>
    internal static void Write(ConfigurationFiles files, string target, byte[] bytes, bool keepPrevious = true)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(bytes);
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempSuffix);
        var backup = keepPrevious ? target + PreviousSuffix : null;
        var created = false;
        var hadTarget = false;
        try
        {
            using (var file = files.CreateNew(temp))
            {
                created = true;
                CopyUnixMode(target, temp);
                files.Write(file, bytes);
                file.Flush(true);
            }

            hadTarget = File.Exists(target);
            if (hadTarget)
            {
                files.Replace(temp, target, backup);
            }
            else
            {
                files.Move(temp, target);
            }
        }
        catch
        {
            if (created && hadTarget && !File.Exists(target) && TryFinishSwap(files, temp, target, backup))
            {
                return;   // the complete new file is in place and the old one is the backup: the save is done
            }

            if (created)
            {
                TryDelete(files, temp);
            }

            throw;
        }
    }

    /// <summary>
    /// Copies a settings file that could not be read to <c>&lt;file&gt;.unreadable-&lt;UTC yyyyMMddTHHmmssfffZ&gt;.xml</c>,
    /// adding <c>-2</c>, <c>-3</c>… when that name is taken. Every copy is a new file, so no copy overwrites an earlier
    /// one; a copy that fails midway is deleted (best effort) and the failure thrown.
    /// </summary>
    /// <param name="files">The file operations.</param>
    /// <param name="target">The unreadable settings file.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The copy's file name (no folder).</returns>
    internal static string KeepUnreadable(ConfigurationFiles files, string target, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(files);
        var stem = target + UnreadableInfix + utcNow.ToString(CopyTimeFormat, CultureInfo.InvariantCulture);
        using var source = files.OpenRead(target);
        for (var n = 1; n <= MaxCopiesPerTime; n++)
        {
            var copy = n == 1 ? stem + ".xml" : string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.xml");
            if (File.Exists(copy))
            {
                continue;
            }

            FileStream output;
            try
            {
                output = files.CreateNew(copy);
            }
            catch (IOException) when (File.Exists(copy))
            {
                continue;   // taken between the check and the create
            }

            try
            {
                using (output)
                {
                    CopyUnixMode(target, copy);
                    files.Copy(source, output);
                    output.Flush(true);
                }
            }
            catch
            {
                TryDelete(files, copy);
                throw;
            }

            return Path.GetFileName(copy);
        }

        throw new IOException("Every name for a copy of the unreadable configuration file at this time is taken");
    }

    /// <summary>
    /// A deep copy of a configuration, through the same XML shape the file has, so a change made to the copy never
    /// reaches the configuration in effect until it is saved and assigned.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The copy.</returns>
    internal static PluginConfiguration Copy(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var buffer = new MemoryStream();
        CopySerializer.Serialize(buffer, config);
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer);
        return (PluginConfiguration)CopySerializer.Deserialize(reader)!;
    }

    /// <summary>
    /// Finishes a swap that failed after the old settings file was moved away (only the temp file and the backup are
    /// left): renames the complete new file into place, or else renames the backup back. Best effort; never throws.
    /// </summary>
    /// <returns>True when the new file is in place (the save is done); false when the old one is back, or neither.</returns>
    private static bool TryFinishSwap(ConfigurationFiles files, string temp, string target, string? backup)
    {
        try
        {
            if (File.Exists(temp))
            {
                files.Move(temp, target);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the backup below.
        }

        try
        {
            if (backup is not null && File.Exists(backup) && !File.Exists(target))
            {
                files.Move(backup, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to try: the failure that brought us here is thrown, and both files stay on disk.
        }

        return false;
    }

    /// <summary>
    /// Gives a new file the Unix permissions of the file it copies or replaces, so a save never widens who can read the
    /// settings (a new file gets the process umask: 0600 would become 0664). Nothing on Windows or without a source file.
    /// </summary>
    private static void CopyUnixMode(string source, string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(source))
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(source));
        }
    }

    private static void TryDelete(ConfigurationFiles files, string path)
    {
        try
        {
            files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover new file is never read; the failure that brought us here is what is thrown.
        }
    }
}
