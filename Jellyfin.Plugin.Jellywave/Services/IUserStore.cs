using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The slice of user management the access granter needs. Lets tests run the full pass without Jellyfin. Only the admin
/// route's granter and the station route check may take it (PolicyWriterDependencyTests); everything else that only reads
/// users takes <see cref="IUserReader"/>.
/// </summary>
public interface IUserStore : IUserReader
{
    /// <summary>Gets the ids of all users (a snapshot; entities are re-read one at a time before writing).</summary>
    /// <returns>The user ids.</returns>
    IEnumerable<Guid> UserIds();

    /// <summary>
    /// Persists a user entity. Must not publish user events (Jellyfin's <c>UpdateUserAsync</c> publishes none;
    /// <c>UpdatePolicyAsync</c> would): <see cref="AccessGranter"/> holds its gate around this call, and any event
    /// consumer that called back into the granter would wait on that gate forever. The plugin registers no user
    /// event consumer (card 15b); this keeps a future one from deadlocking.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(User user);
}
