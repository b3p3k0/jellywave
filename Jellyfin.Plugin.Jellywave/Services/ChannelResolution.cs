using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What <see cref="LiveItemFinder.Resolve"/> found for the configured station name: the channel entity, nothing yet,
/// or a name collision with another plugin's channel. On a collision <see cref="Channel"/> is always null, so a caller
/// that only reads the entity fails closed without checking.
/// </summary>
public sealed class ChannelResolution
{
    /// <summary>
    /// The fixed status text for a collision, shown on the configuration page (status line and grant result). Card L11:
    /// the page says "tile", not "channel" (Jellyfin shows a channel as a tile in My Media).
    /// </summary>
    public const string CollisionStatus = "another plugin's tile has the same name";

    private ChannelResolution(Channel? channel, string? collidesWith)
    {
        Channel = channel;
        CollidesWith = collidesWith;
    }

    /// <summary>Gets the result while Jellyfin has not created the channel entity yet.</summary>
    public static ChannelResolution Missing { get; } = new(null, null);

    /// <summary>Gets the channel entity, or null when it is missing or the name collides.</summary>
    public Channel? Channel { get; }

    /// <summary>Gets the other plugin channel's name when the derived ids collide; null otherwise.</summary>
    public string? CollidesWith { get; }

    /// <summary>Gets a value indicating whether another registered channel maps to the same channel id.</summary>
    public bool Collides => CollidesWith is not null;

    /// <summary>
    /// The channel entity was found under the derived id and no other channel claims that id.
    /// </summary>
    /// <param name="channel">The channel entity.</param>
    /// <returns>The resolution.</returns>
    public static ChannelResolution Found(Channel channel) => new(channel, null);

    /// <summary>
    /// Another registered channel maps to the same id; the entity is withheld.
    /// </summary>
    /// <param name="otherName">The other channel's name, for the log.</param>
    /// <returns>The resolution.</returns>
    public static ChannelResolution Collision(string otherName) => new(null, otherName);
}
