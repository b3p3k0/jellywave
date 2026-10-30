using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// <see cref="IUserReader"/> over Jellyfin's <see cref="IUserManager"/>: reads one user and nothing else. Deliberately not
/// an <see cref="IUserStore"/>, so code holding it (the channel's <c>IsEnabledFor</c>) cannot be cast to a policy writer.
/// </summary>
public sealed class UserManagerReader : IUserReader
{
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserManagerReader"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    public UserManagerReader(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <inheritdoc />
    public User? GetUser(Guid id) => _userManager.GetUserById(id);
}
