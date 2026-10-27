using System;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// An <see cref="IChannelManager"/> whose <c>GetChannelsInternalAsync</c> counts calls and runs a callback (the test
/// creates the entity there, as Jellyfin would). Anything else throws.
/// </summary>
public class CountingChannelManager : DispatchProxy
{
    public int Creations { get; private set; }

    public Action OnCreate { get; set; } = () => { };

    public IChannelManager Manager => (IChannelManager)(object)this;

    public static CountingChannelManager Create() => (CountingChannelManager)(object)Create<IChannelManager, CountingChannelManager>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IChannelManager.GetChannelsInternalAsync))
        {
            throw new NotSupportedException(targetMethod?.Name);
        }

        Creations++;
        OnCreate();
        return Task.FromResult(new QueryResult<Channel>());
    }
}
