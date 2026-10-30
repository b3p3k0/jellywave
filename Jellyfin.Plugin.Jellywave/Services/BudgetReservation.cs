using System;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What <see cref="UserBudgets.TryReserve"/> took from a user's budgets for one station call. It stays spent when the
/// call went out (an answer, a timeout, a connection lost after sending). <see cref="Refund"/> gives it all back when
/// nothing reached the station (no connection, no name, or this server's own limiter refused it);
/// <see cref="RefundOwnWindows"/> gives back only the call kind's own windows (request, like) when the station refused
/// it, and the general window stays spent, so a station's refusals cannot be looped for free (card R2a fix round).
/// </summary>
public sealed class BudgetReservation
{
    private readonly UserBudgets _budgets;
    private int _refunded;
    private int _ownRefunded;

    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetReservation"/> class. Only <see cref="UserBudgets"/> makes one.
    /// </summary>
    /// <param name="budgets">The budgets it was taken from.</param>
    /// <param name="userId">The user.</param>
    /// <param name="windows">The budget windows it counts in.</param>
    /// <param name="at">When it was taken.</param>
    internal BudgetReservation(UserBudgets budgets, Guid userId, int[] windows, DateTimeOffset at)
    {
        _budgets = budgets;
        UserId = userId;
        Windows = windows;
        At = at;
    }

    /// <summary>Gets the user.</summary>
    internal Guid UserId { get; }

    /// <summary>Gets the budget windows it counts in.</summary>
    internal int[] Windows { get; }

    /// <summary>Gets when it was taken.</summary>
    internal DateTimeOffset At { get; }

    /// <summary>Gives the reservation back to every window it counts in; a second call, or one after
    /// <see cref="RefundOwnWindows"/>, gives back only what is still held.</summary>
    public void Refund()
    {
        if (Interlocked.Exchange(ref _refunded, 1) == 0)
        {
            _budgets.Refund(this, includeGeneral: true, includeOwn: Interlocked.Exchange(ref _ownRefunded, 1) == 0);
        }
    }

    /// <summary>Gives back the call kind's own windows only (request, like); the general window stays spent. Once.</summary>
    public void RefundOwnWindows()
    {
        if (Volatile.Read(ref _refunded) == 0 && Interlocked.Exchange(ref _ownRefunded, 1) == 0)
        {
            _budgets.Refund(this, includeGeneral: false, includeOwn: true);
        }
    }
}
