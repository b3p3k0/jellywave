using System.IO;

namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// The file operations behind <see cref="ConfigurationFile"/>: the settings writer and the copy of an unreadable file.
/// One virtual method per step, so a test can make any single step fail; the plugin uses these as they are.
/// </summary>
internal class ConfigurationFiles
{
    /// <summary>Creates a file that must not exist yet, for writing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The open file.</returns>
    public virtual FileStream CreateNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>Opens an existing file for reading.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The open file.</returns>
    public virtual FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Writes the bytes to an open file.</summary>
    /// <param name="file">The file.</param>
    /// <param name="bytes">The bytes.</param>
    public virtual void Write(FileStream file, byte[] bytes) => file.Write(bytes);

    /// <summary>Copies one open file into another.</summary>
    /// <param name="source">The file read.</param>
    /// <param name="target">The file written.</param>
    public virtual void Copy(FileStream source, FileStream target) => source.CopyTo(target);

    /// <summary>Puts <paramref name="source"/> in the place of <paramref name="target"/> in one step, keeping the old target as <paramref name="backup"/>.</summary>
    /// <param name="source">The new file.</param>
    /// <param name="target">The file replaced.</param>
    /// <param name="backup">Where the old target goes, or null to keep no backup.</param>
    public virtual void Replace(string source, string target, string? backup) => File.Replace(source, target, backup);

    /// <summary>Renames <paramref name="source"/> to <paramref name="target"/>, which must not exist.</summary>
    /// <param name="source">The new file.</param>
    /// <param name="target">Its name.</param>
    public virtual void Move(string source, string target) => File.Move(source, target);

    /// <summary>Deletes a file; nothing happens when it does not exist.</summary>
    /// <param name="path">The file.</param>
    public virtual void Delete(string path) => File.Delete(path);
}
