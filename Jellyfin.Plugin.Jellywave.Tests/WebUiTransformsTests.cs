using Jellyfin.Plugin.Jellywave.Web;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class WebUiTransformsTests
{
    [Fact]
    public void Index_html_gets_the_stylesheet_and_a_synchronous_script_before_head_closes()
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><script defer=\"defer\" src=\"main.js\"></script></body></html>";
        var result = WebUiTransforms.IndexHtml(new TransformPayload { Contents = html });
        var v = WebUiTransforms.AssetVersion();
        Assert.Contains($"<link rel=\"stylesheet\" href=\"../Jellywave/ui.css?v={v}\" /><script src=\"../Jellywave/ui.js?v={v}\"></script></head>", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("defer src=\"../Jellywave", result, System.StringComparison.Ordinal);
        Assert.Equal(result, WebUiTransforms.IndexHtml(new TransformPayload { Contents = result }));   // idempotent
    }

    [Fact]
    public void Index_html_without_a_head_is_returned_unchanged()
    {
        Assert.Equal("<p>odd</p>", WebUiTransforms.IndexHtml(new TransformPayload { Contents = "<p>odd</p>" }));
        Assert.Equal(string.Empty, WebUiTransforms.IndexHtml(new TransformPayload()));
    }

    [Fact]
    public void Config_json_gets_the_plugin_name_first_in_the_plugins_array()
    {
        var json = "{\n  \"menuLinks\": [],\n  \"plugins\": [\n    \"htmlAudioPlayer/plugin\",\n    \"syncPlay/plugin\"\n  ]\n}";
        var result = WebUiTransforms.ConfigJson(new TransformPayload { Contents = json });
        Assert.Contains("\"plugins\": [\n    \"JellywaveUi\", \"htmlAudioPlayer/plugin\"", result, System.StringComparison.Ordinal);
        Assert.Equal(result, WebUiTransforms.ConfigJson(new TransformPayload { Contents = result }));
        Assert.Equal("{\"plugins\":[\"JellywaveUi\", ]}", WebUiTransforms.ConfigJson(new TransformPayload { Contents = "{\"plugins\":[]}" }));
    }

    [Fact]
    public void Config_json_without_a_plugins_array_is_returned_unchanged()
    {
        Assert.Equal("{\"themes\":[]}", WebUiTransforms.ConfigJson(new TransformPayload { Contents = "{\"themes\":[]}" }));
    }
}
