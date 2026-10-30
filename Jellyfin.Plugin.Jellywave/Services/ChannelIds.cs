using System;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Derives a channel id from a channel name exactly as Jellyfin does (<c>ChannelManager.GetInternalChannelId</c>, 12.3):
/// <c>GetNewItemId("Channel " + name, typeof(Channel))</c>. Going through the library manager keeps the server's own case
/// rule (<c>EnableCaseSensitiveItemIds</c>).
/// </summary>
public static class ChannelIds
{
    /// <summary>
    /// The prefix <c>ChannelManager.GetInternalChannelId</c> (Jellyfin 12.3) puts before the channel name.
    /// </summary>
    public const string ChannelKeyPrefix = "Channel ";

    /// <summary>
    /// Derives the channel id for a name.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="name">The channel name.</param>
    /// <returns>The id Jellyfin gives a channel of that name.</returns>
    public static Guid Derive(ILibraryManager libraryManager, string? name)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        return libraryManager.GetNewItemId(ChannelKeyPrefix + name, typeof(Channel));
    }
}
