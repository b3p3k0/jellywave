using System;
using System.Reflection;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Another plugin's <see cref="IChannel"/>: only its name is read by the collision check; anything else throws.
/// </summary>
public class OtherPluginChannel : DispatchProxy
{
    private string _name = string.Empty;

    public static IChannel Named(string name)
    {
        var channel = Create<IChannel, OtherPluginChannel>();
        ((OtherPluginChannel)(object)channel)._name = name;
        return channel;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == "get_" + nameof(IChannel.Name) ? _name : throw new NotSupportedException(targetMethod?.Name);
}
