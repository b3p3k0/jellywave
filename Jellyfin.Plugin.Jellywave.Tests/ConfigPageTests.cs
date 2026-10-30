using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L3: the configuration page as embedded. Station and probe text reaches it through <c>textContent</c> or <c>value</c>
/// only (THREAT-MODEL boundary 5), and it calls the admin routes that exist.
/// </summary>
public class ConfigPageTests
{
    private static string Page()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellywave.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("setHTML")]
    [InlineData("setHTMLUnsafe")]
    [InlineData("parseHTMLUnsafe")]
    [InlineData("createContextualFragment")]
    [InlineData("parseFromString")]
    [InlineData("srcdoc")]
    [InlineData(".html(")]
    public void The_page_has_no_markup_sink(string sink)
        => Assert.DoesNotContain(sink, Page(), StringComparison.Ordinal);

    [Fact]
    public void The_page_uses_the_probe_route_and_not_the_removed_one()
    {
        var page = Page();
        Assert.Contains("'Jellywave/Probe'", page, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("'Jellywave/Station'"), page);
    }

    [Fact]
    public void New_station_ids_are_n_format_guids_from_get_random_values()
    {
        // randomUUID needs a secure context, and Jellyfin is often served over plain http on the LAN.
        var page = Page();
        Assert.Contains("crypto.getRandomValues", page, StringComparison.Ordinal);
        Assert.DoesNotContain("randomUUID(", page, StringComparison.Ordinal);
    }
}
