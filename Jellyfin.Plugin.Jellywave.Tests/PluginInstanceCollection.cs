using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Tests that build a real <see cref="Plugin"/> set the static <see cref="Plugin.Instance"/>, which the DI constructors and
/// the channel read. They run alone, after the parallel collections, and reset it afterwards.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}
