using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What the now-playing poller last did, for the configuration page. Written by the poller, read by the admin
/// endpoint; a snapshot, so the page never needs the server log.
/// </summary>
public sealed class PollerHealth
{
    private readonly object _gate = new();
    private bool _running;
    private bool _reachable = true;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastErrorUtc;
    private string? _lastError;

    /// <summary>Gets a value indicating whether the poll loop is running.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Gets a value indicating whether the last poll reached the station.</summary>
    public bool Reachable
    {
        get
        {
            lock (_gate)
            {
                return _reachable;
            }
        }
    }

    /// <summary>Gets the time of the last successful poll.</summary>
    public DateTime? LastSuccessUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastSuccessUtc;
            }
        }
    }

    /// <summary>Gets the time of the last failure.</summary>
    public DateTime? LastErrorUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastErrorUtc;
            }
        }
    }

    /// <summary>Gets the last failure's message.</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
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
