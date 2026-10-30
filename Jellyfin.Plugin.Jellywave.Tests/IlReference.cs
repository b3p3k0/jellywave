using System.Reflection;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>One token in a method's IL (<see cref="IlReferences"/>).</summary>
/// <param name="Method">The compiled method the IL belongs to.</param>
/// <param name="Owner">The source method it belongs to (a state machine or lambda belongs to the method that declared it).</param>
/// <param name="Member">The member or type the token resolves to, or null when it does not resolve.</param>
/// <param name="Token">The metadata token.</param>
internal sealed record IlReference(MethodBase Method, string Owner, MemberInfo? Member, int Token);
