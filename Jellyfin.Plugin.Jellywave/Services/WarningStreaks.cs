using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Remembers which warnings are standing, so a condition that holds across many checks is logged once per streak:
/// <see cref="Begin"/> answers true only when a key is new or its message changed, and <see cref="Retain"/> ends every
/// streak not seen in the latest check. Thread-safe.
/// </summary>
public sealed class WarningStreaks
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _standing = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that a warning holds.
    /// </summary>
    /// <param name="key">What the warning is about (for example a station id).</param>
    /// <param name="message">The warning.</param>
    /// <returns>True when the warning should be logged: the key is new or its message changed.</returns>
    public bool Begin(string key, string message)
    {
        lock (_gate)
        {
            if (_standing.TryGetValue(key, out var current) && string.Equals(current, message, StringComparison.Ordinal))
            {
                return false;
            }

            _standing[key] = message;
            return true;
        }
    }

    /// <summary>
    /// Ends the streak of one key, so the next <see cref="Begin"/> for it logs again.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True when a streak was standing.</returns>
    public bool End(string key)
    {
        lock (_gate)
        {
            return _standing.Remove(key);
        }
    }

    /// <summary>
    /// Ends every streak whose key is not in <paramref name="seen"/>.
    /// </summary>
    /// <param name="seen">The keys that still hold.</param>
    public void Retain(IReadOnlySet<string> seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        lock (_gate)
        {
            foreach (var key in new List<string>(_standing.Keys))
            {
                if (!seen.Contains(key))
                {
                    _standing.Remove(key);
                }
            }
        }
    }
}
