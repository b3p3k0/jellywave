using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What the now-playing poller last did, for the configuration page. Written by the poller, read by the admin
/// endpoint as one snapshot, so the page never needs the server log.
/// </summary>
public sealed class PollerHealth
{
    private readonly object _gate = new();
    private bool _running;
    private bool? _reachable;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastErrorUtc;
    private string? _lastError;

    /// <summary>
    /// Reads every field under one lock.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public PollerHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PollerHealthSnapshot
            {
                Running = _running,
                Reachable = _reachable,
                LastSuccessUtc = _lastSuccessUtc,
                LastErrorUtc = _lastErrorUtc,
                LastError = _lastError
            };
        }
    }

    /// <summary>Records that the loop started or stopped.</summary>
    /// <param name="running">Whether it is running.</param>
    public void SetRunning(bool running)
    {
        lock (_gate)
        {
            _running = running;
        }
    }

    /// <summary>Records a successful poll.</summary>
    /// <param name="nowUtc">The time.</param>
    public void Success(DateTime nowUtc)
    {
        lock (_gate)
        {
            _reachable = true;
            _lastSuccessUtc = nowUtc;
        }
    }

    /// <summary>Records a failed poll.</summary>
    /// <param name="nowUtc">The time.</param>
    /// <param name="message">What went wrong, in one line.</param>
    public void Failure(DateTime nowUtc, string message)
    {
        lock (_gate)
        {
            _reachable = false;
            _lastErrorUtc = nowUtc;
            _lastError = message;
        }
    }
}
