using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Grants channel access to users as they are created, so provisioning tools cannot leave them out.
/// </summary>
public sealed class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly AccessGranter _granter;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    /// <param name="granter">The granter.</param>
    /// <param name="logger">The logger.</param>
    public UserCreatedConsumer(AccessGranter granter, ILogger<UserCreatedConsumer> logger)
    {
        _granter = granter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        try
        {
            await _granter.GrantAsync(eventArgs.Argument, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let a plugin failure break user creation.
            _logger.LogError(ex, "Channel access grant for a new user failed");
        }
    }
}
