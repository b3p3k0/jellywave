using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Jellyfin offers a build to every server at or above its targetAbi, and the runtime only binds an assembly
/// reference to a loaded assembly of the same or a newer version. So no Jellyfin reference in the built DLL may be
/// newer than targetAbi, or servers between the two would be offered a plugin they cannot load.
/// </summary>
public class AbiTests
{
    [Fact]
    public void Jellyfin_references_are_not_newer_than_targetAbi()
    {
        var targetAbi = Version.Parse(ReadTargetAbi());
        var references = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name is not null && (a.Name.StartsWith("Jellyfin.", StringComparison.Ordinal) || a.Name.StartsWith("MediaBrowser.", StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(references);
        Assert.All(references, a => Assert.True(a.Version <= targetAbi, $"{a.Name} {a.Version} is newer than targetAbi {targetAbi}"));
    }

    /// <summary>
    /// build.yaml values are single-line by rule (scripts/lib.sh parses it the same way), so one anchored match suffices.
    /// </summary>
    private static string ReadTargetAbi()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var match = Regex.Match(File.ReadAllText(Path.Combine(dir.FullName, "build.yaml")), "^targetAbi: \"([0-9.]+)\"$", RegexOptions.Multiline);
        Assert.True(match.Success, "build.yaml has no targetAbi line");
        return match.Groups[1].Value;
    }
}
