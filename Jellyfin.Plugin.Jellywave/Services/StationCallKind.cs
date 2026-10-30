namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The user-driven station calls the station routes make (card R2a), for <see cref="UserBudgets"/> and
/// <see cref="StationCalls"/>. The poller, the title sampler, the schedule and "Reload status" are not among them: they
/// keep their own paths and bounds.
/// </summary>
public enum StationCallKind
{
    /// <summary>The like status of the track on air (<c>GET /api/like</c>).</summary>
    LikeStatus,

    /// <summary>A like (<c>POST /api/like</c>).</summary>
    Like,

    /// <summary>The lyrics of the track on air.</summary>
    Lyrics,

    /// <summary>A DJ portrait.</summary>
    Avatar,

    /// <summary>A listener request (<c>POST /api/request</c>).</summary>
    Request,

    /// <summary>A listener request's status.</summary>
    RequestStatus
}
