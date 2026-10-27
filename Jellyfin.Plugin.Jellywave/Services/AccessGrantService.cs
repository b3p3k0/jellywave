using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Runs one access-grant pass after core startup, so an upgrade or a toggle saved before a restart takes effect.
/// </summary>
public sealed class AccessGrantService : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromSeconds(2);

    private readonly AccessGranter _granter;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<AccessGrantService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGrantService"/> class.
    /// </summary>
    /// <param name="granter">The granter.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    public AccessGrantService(AccessGranter granter, IServerApplicationHost applicationHost, ILogger<AccessGrantService> logger)
    {
        _granter = granter;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await Task.WhenAny(_run, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_applicationHost.CoreStartupHasCompleted)
            {
                await Task.Delay(StartupProbeDelay, cancellationToken).ConfigureAwait(false);
            }

            await _granter.GrantAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the pass could run; the next start runs it.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup channel access pass failed");
        }
    }
}
