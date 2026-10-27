using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Re-grants after a policy write. Jellyfin publishes this event from <c>UpdatePolicyAsync</c>, which
/// replaces the whole enabled-channel list (the dashboard's new-user page and provisioning tools do this
/// right after creating a user). The plugin's own saves go through <c>UpdateUserAsync</c>, which publishes
/// nothing, and a re-grant on an already visible user writes nothing, so there is no loop.
/// </summary>
public sealed class UserUpdatedConsumer : IEventConsumer<UserUpdatedEventArgs>
{
    private readonly AccessGranter _granter;
    private readonly ILogger<UserUpdatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserUpdatedConsumer"/> class.
    /// </summary>
    /// <param name="granter">The granter.</param>
    /// <param name="logger">The logger.</param>
    public UserUpdatedConsumer(AccessGranter granter, ILogger<UserUpdatedConsumer> logger)
    {
        _granter = granter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserUpdatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        try
        {
            await _granter.GrantAsync(eventArgs.Argument.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel access grant after a user update failed");
        }
    }
}
