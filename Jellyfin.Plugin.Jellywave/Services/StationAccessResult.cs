using System;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What <see cref="StationAccess.Check"/> decided, and for an allowed caller what it resolved on the way.
/// </summary>
/// <param name="Reason"><see cref="StationAccessReason.Allowed"/>, or the rule that refused the caller.</param>
/// <param name="UserId">The caller's user id when allowed; <see cref="Guid.Empty"/> otherwise.</param>
/// <param name="Channel">The channel entity when allowed; null otherwise.</param>
public sealed record StationAccessResult(StationAccessReason Reason, Guid UserId, Channel? Channel);
