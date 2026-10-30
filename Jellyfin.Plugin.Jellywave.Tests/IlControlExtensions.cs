namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// An extension helper the station route allow-list does not name (card S-b control; never run). Extension methods must
/// live in a top-level static class, so this one cannot sit with the other controls in <see cref="StationRouteIlTests"/>.
/// </summary>
internal static class IlControlExtensions
{
    public static string Unlisted(this string value) => value;
}
