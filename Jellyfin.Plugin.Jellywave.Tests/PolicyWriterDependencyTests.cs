using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15b: only the admin route may reach the code that writes user policies. A hosted service or event consumer
/// that took <see cref="AccessGranter"/> (or the user store under it) could grant in the background again, and the
/// registrator test would not see it. Every constructor in the plugin assembly is checked, generic arguments included
/// (<c>Func&lt;AccessGranter&gt;</c>, <c>Lazy&lt;IUserStore&gt;</c>). A service locator (<see cref="IServiceProvider"/>)
/// can resolve anything at run time, out of this scan's sight, so it is guarded the same way (card 17). Calls made through
/// <c>HttpContext.RequestServices</c> never show in a constructor; <see cref="ServiceLocatorSourceTests"/> scans the
/// source for them (allowed: <c>StationAccessAttribute</c> resolving <see cref="StationAccess"/>, <see cref="LiveItemFinder"/>
/// resolving the registered channels).
/// </summary>
public class PolicyWriterDependencyTests
{
    /// <summary>Which plugin types may take each guarded dependency as a constructor parameter.</summary>
    private static readonly Dictionary<Type, Type[]> Allowed = new()
    {
        [typeof(AccessGranter)] = [typeof(JellywaveController)],   // POST /Jellywave/GrantAccess, admin only
        [typeof(IUserStore)] = [typeof(AccessGranter), typeof(StationAccess)],   // StationAccess reads users for the route check
        // UserManagerReader only calls GetUserById (the read-only IUserReader for the channel's IsEnabledFor, card L1); it is not an IUserStore.
        [typeof(IUserManager)] = [typeof(UserManagerStore), typeof(UserManagerReader)],
        [typeof(UserManagerStore)] = [],   // registered behind IUserStore only; never taken directly
        [typeof(IServiceProvider)] = [typeof(LiveItemFinder)],   // resolves the registered channels lazily; a new service locator could reach the granter unseen
    };

    private static IEnumerable<Type> Mentioned(Type type)
    {
        yield return type;
        foreach (var arg in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (var inner in Mentioned(arg))
            {
                yield return inner;
            }
        }

        if (type.HasElementType)
        {
            foreach (var inner in Mentioned(type.GetElementType()!))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public void Only_the_allowed_types_take_a_policy_writing_dependency()
    {
        var offenders = typeof(Plugin).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .SelectMany(p => Mentioned(p.ParameterType))
                .Where(Allowed.ContainsKey)
                .Where(dep => dep != t && !Allowed[dep].Contains(t))   // a type may name itself (ILogger<AccessGranter>)
                .Select(dep => t.FullName + " takes " + dep.Name))
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_read_only_user_reader_cannot_write()
    {
        Assert.False(typeof(IUserStore).IsAssignableFrom(typeof(UserManagerReader)));
        Assert.DoesNotContain(typeof(UserManagerReader).GetMethods(), m => m.Name.Contains("Save", StringComparison.Ordinal) || m.Name.Contains("Update", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allowed_types_still_take_them()
    {
        // Control: the scan sees constructor parameters at all, so an empty offender list above means something.
        Assert.Contains(
            typeof(JellywaveController).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(AccessGranter));
        Assert.Contains(typeof(UserManagerStore).GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IUserManager));
        Assert.Contains(typeof(LiveItemFinder).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IServiceProvider));
    }
}
