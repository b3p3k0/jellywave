using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 review: a service-locator call (<c>GetRequiredService</c>, <c>GetService</c>, <c>GetServices</c>) can resolve
/// the granter or the user store at run time, out of sight of the constructor scan in
/// <see cref="PolicyWriterDependencyTests"/>. This scans the plugin's source for such calls and allows only the known
/// sites, by file and resolved type.
/// </summary>
public class ServiceLocatorSourceTests
{
    /// <summary>The known service-locator sites: (file name without extension, resolved type).</summary>
    private static readonly (string File, string Type)[] Allowed =
    [
        ("StationAccessAttribute", "StationAccess"),   // an attribute filter cannot take constructor dependencies
        ("StationRouteAttribute", "StationResolver"),   // the same, for the station route filter (card L5)
        ("StationRouteAttribute", "StationDemand"),   // the same filter records a station's open page (card L14)
        ("LiveItemFinder", "IChannel"),   // registered channels resolved lazily (startup order)
    ];

    private static readonly Regex Call = new(@"\b(?:GetRequiredKeyedService|GetKeyedServices|GetKeyedService|GetRequiredService|GetServices|GetServiceOrCreateInstance|GetService|CreateInstance|CreateFactory)\b\s*(?:<\s*(?<type>[\w.]+)\s*>|\(\s*typeof\s*\(\s*(?<type>[\w.]+)\s*\))?", RegexOptions.Compiled);

    private static IReadOnlyList<(string File, string Type)> Sites()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var source = Path.Combine(dir.FullName, "Jellyfin.Plugin.Jellywave");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep, StringComparison.Ordinal) && !f.Contains(sep + "bin" + sep, StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .SelectMany(line => Call.Matches(line))
                .Select(m => (Path.GetFileNameWithoutExtension(f), m.Groups["type"].Success ? m.Groups["type"].Value.Split('.').Last() : "?")))
            .ToList();
    }

    [Fact]
    public void Only_the_known_sites_use_a_service_locator()
    {
        var offenders = Sites().Where(s => !Allowed.Contains(s)).Select(s => s.File + " resolves " + s.Type).Distinct().ToArray();

        Assert.True(offenders.Length == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_scan_sees_the_known_sites()
    {
        // Control: an empty offender list above means something only if the scan finds the calls that do exist.
        var sites = Sites();
        Assert.All(Allowed, a => Assert.Contains(a, sites));
    }
}
