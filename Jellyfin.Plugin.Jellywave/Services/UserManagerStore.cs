using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// <see cref="IUserStore"/> over Jellyfin's <see cref="IUserManager"/>. GetUserById reads from the database
/// without tracking, so each user is current at the moment it is read.
/// </summary>
public sealed class UserManagerStore : IUserStore
{
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserManagerStore"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    public UserManagerStore(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <inheritdoc />
    public IEnumerable<Guid> UserIds() => _userManager.GetUsersIds();

    /// <inheritdoc />
    public User? GetUser(Guid id) => _userManager.GetUserById(id);

    /// <inheritdoc />
    /// <remarks>
    /// UpdateUserAsync, never UpdatePolicyAsync: this must not publish user events, because AccessGranter holds its
    /// gate around the save (see <see cref="IUserStore.SaveAsync"/>).
    /// </remarks>
    public Task SaveAsync(User user) => _userManager.UpdateUserAsync(user);
}
