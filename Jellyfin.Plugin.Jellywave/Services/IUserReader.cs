using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Reads users and nothing else: the narrow interface for code that must decide on a user's policy without being able
/// to save one (the channel's <c>IsEnabledFor</c>). Registered as <see cref="UserManagerReader"/>, which is not an
/// <see cref="IUserStore"/>.
/// </summary>
public interface IUserReader
{
    /// <summary>Reads one user fresh, or null when it no longer exists.</summary>
    /// <param name="id">The user id.</param>
    /// <returns>The user.</returns>
    User? GetUser(Guid id);
}
