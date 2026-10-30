using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L5 (RA final round, S2): what the station page controller's own code reaches, read from its IL and that of its
/// compiler-generated nested types (async state machines, lambdas). The routes reach a station only through
/// <c>CallerStation()</c> (the filter's station), and only <c>CallerStation</c>, <c>BaseUrlOf</c>, <c>KeyOf</c> and
/// <c>NameOf</c> open it. No path to the configuration, the resolver, the request services, a hand-made URL, the probe or
/// a station's origin text. <see cref="StationList"/>, the one dependency that holds the resolver, is scanned too: it may
/// read station ids, names, kinds, keys and the format, and the header fields through <see cref="StationText"/>, never a
/// stream or station URL. A control class proves the scan sees each bypass.
/// <para>
/// Card S-b turns the helper gap into an allow-list (<see cref="StationRouteAllowList"/>, checked by
/// <see cref="PluginCalls"/>): every plugin member the controller, its nested types and <see cref="StationList"/>
/// reference must be listed with a reason, and each listed member is followed through the plugin's code fully
/// transitively. The list stays one level deep (what the scanned types call themselves, which is what a reviewer
/// decides); the walk goes all the way down, because a listed helper that calls an unlisted one that reads
/// <c>Plugin.Instance.Configuration</c>, the resolver, a station or the item bag is exactly the gap one level would leave.
/// A sink the walk finds must be named on the member's line, with the reason. After the S-b review the plugin types the
/// framework builds or reads for the scanned code (type tokens, generic arguments, parameter and return types) must be
/// listed data types, and each listed data type is walked whole, so an initializer or a getter only the serializer calls
/// is read too. <see cref="PluginCalls"/> says exactly what the walk follows and what it cannot see. The deny rules above
/// still apply.
/// </para>
/// </summary>
public class StationRouteIlTests
{
    /// <summary>Types the controller's code may not touch at all: no member, no type token, no generic argument.</summary>
    private static readonly Type[] ControllerForbidden =
    [
        typeof(Plugin), typeof(PluginConfiguration), typeof(StationDefinition), typeof(StationSources), typeof(StationUrls),
        typeof(StationResolver), typeof(Uri), typeof(UriBuilder), typeof(IServiceProvider), typeof(ServiceProviderServiceExtensions),
        typeof(ActivatorUtilities), typeof(Activator), typeof(MethodBase), typeof(MethodInfo), typeof(ConstructorInfo),
        typeof(PropertyInfo), typeof(FieldInfo), typeof(MemberInfo)
    ];

    /// <summary>The controller's methods that may open the filter's station.</summary>
    private static readonly string[] StationOpeners = ["CallerStation", "BaseUrlOf", "KeyOf", "NameOf"];

    /// <summary>
    /// The controller's methods that may use <c>HttpContext.Items</c> (card L7, S-a from the L5 review): the accessors of
    /// what the filters left there (card L6 added the plain stream's), each parameterless and returning a value. Anywhere else a station could be written
    /// into, or read out of, <c>Items</c> through <c>object</c>, which the type rules above cannot see.
    /// </summary>
    private static readonly string[] ItemsAccessors = ["CallerId", "CallerStation", "CallerPlainStation", "CallerChannel"];

    /// <summary>Members forbidden by name, whatever the method: (declaring type, member name, reason).</summary>
    private static readonly (Type Type, string Name, string Why)[] ForbiddenMembers =
    [
        (typeof(Microsoft.AspNetCore.Http.HttpContext), "get_RequestServices", "a service locator"),
        (typeof(StationClient), nameof(StationClient.ProbeStreamAsync), "the admin probe (its answer carries station-chosen URLs)"),
        (typeof(StationKey), "get_Origin", "a station's base URL as text"),
        (typeof(Type), nameof(Type.GetType), "a type by name"),
        (typeof(Type), nameof(Type.GetMethod), "reflection"),
        (typeof(Type), nameof(Type.GetMethods), "reflection"),
        (typeof(Type), nameof(Type.GetProperty), "reflection"),
        (typeof(Type), nameof(Type.GetProperties), "reflection"),
        (typeof(Type), nameof(Type.GetField), "reflection"),
        (typeof(Type), nameof(Type.GetFields), "reflection"),
        (typeof(Type), nameof(Type.GetMember), "reflection"),
        (typeof(Type), nameof(Type.GetMembers), "reflection"),
        (typeof(Type), nameof(Type.InvokeMember), "reflection"),
        (typeof(Type), nameof(Type.GetConstructor), "reflection"),
        (typeof(Assembly), nameof(Assembly.GetType), "a type by name"),
        (typeof(Assembly), nameof(Assembly.GetTypes), "reflection"),
        (typeof(Assembly), nameof(Assembly.CreateInstance), "reflection"),
    ];

    private static bool InFamily(Type? type, Type root)
    {
        for (; type is not null; type = type.DeclaringType)
        {
            if (type == root)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A type with its generic arguments and element types, recursively.</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (type.HasElementType)
        {
            foreach (var t in Expand(type.GetElementType()!))
            {
                yield return t;
            }
        }

        foreach (var t in type.IsGenericType ? type.GetGenericArguments().SelectMany(Expand) : [])
        {
            yield return t;
        }
    }

    /// <summary>The item bag's feature types: any token, generic argument or constructor of them counts as using the bag.</summary>
    private static readonly Type[] ItemsTypes = [typeof(Microsoft.AspNetCore.Http.Features.IItemsFeature), typeof(Microsoft.AspNetCore.Http.Features.ItemsFeature)];

    /// <summary>The request's item bag, read or replaced, through the context or through its feature.</summary>
    private static bool UsesItems(MemberInfo member)
        => member.Name is "get_Items" or "set_Items"
            && member.DeclaringType is { } declaring
            && (typeof(Microsoft.AspNetCore.Http.HttpContext).IsAssignableFrom(declaring)
                || typeof(Microsoft.AspNetCore.Http.Features.IItemsFeature).IsAssignableFrom(declaring));

    /// <summary>One of the three accessors itself: declared on the scanned type, by name, parameterless and returning a value.</summary>
    private static bool IsItemsAccessor(MethodBase method, Type root)
        => method is MethodInfo m
            && m.DeclaringType == root
            && ItemsAccessors.Contains(m.Name)
            && m.GetParameters().Length == 0
            && m.ReturnType != typeof(void);

    private static Type? ResultOf(MemberInfo member) => member switch
    {
        MethodInfo m => m.ReturnType,
        FieldInfo f => f.FieldType,
        _ => null
    };

    /// <summary>The rule for a controller-like type: what in its IL breaks "a station only through CallerStation".</summary>
    internal static List<string> ControllerViolations(Type root)
    {
        var violations = new List<string>();
        foreach (var r in IlReferences.Of(root))
        {
            var where = r.Owner + " (" + r.Method.DeclaringType!.Name + "." + r.Method.Name + ")";
            if (r.Member is null)
            {
                violations.Add(where + ": unresolved token 0x" + r.Token.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            var opener = StationOpeners.Contains(r.Owner) && r.Method.DeclaringType == root;
            var core = new List<Type>();
            Type? result = null;
            if (r.Member is Type token)
            {
                core.AddRange(Expand(token));
            }
            else
            {
                core.AddRange(Expand(r.Member.DeclaringType!));
                if (r.Member is MethodInfo { IsGenericMethod: true } generic)
                {
                    core.AddRange(generic.GetGenericArguments().SelectMany(Expand));
                }

                // What a call or field read hands back, unless it is the controller's own (its locals and helpers).
                if (!InFamily(r.Member.DeclaringType, root))
                {
                    result = ResultOf(r.Member);
                }
            }

            var name = r.Member is Type ? r.Member.Name : r.Member.DeclaringType!.Name + "." + r.Member.Name;
            foreach (var t in core.Where(ControllerForbidden.Contains))
            {
                violations.Add(where + ": " + name + " touches " + t.Name);
            }

            if (core.Contains(typeof(StationSource)) && !opener)
            {
                violations.Add(where + ": " + name + " opens a StationSource outside " + string.Join("/", StationOpeners));
            }

            if (result is not null)
            {
                var handed = Expand(result).ToList();
                var openedHere = opener && r.Member.DeclaringType == typeof(StationSource);
                foreach (var t in handed.Where(t => ControllerForbidden.Contains(t) || t == typeof(StationSource)).Where(_ => !openedHere))
                {
                    violations.Add(where + ": " + name + " hands back " + t.Name);
                }
            }

            foreach (var (type, member, why) in ForbiddenMembers)
            {
                if (r.Member.Name == member && r.Member.DeclaringType is { } declaring && type.IsAssignableFrom(declaring))
                {
                    violations.Add(where + ": " + name + " (" + why + ")");
                }
            }

            if ((UsesItems(r.Member) || core.Any(ItemsTypes.Contains)) && !IsItemsAccessor(r.Method, root))
            {
                violations.Add(where + ": " + name + " (HttpContext.Items outside " + string.Join("/", ItemsAccessors) + ")");
            }

            var ns = r.Member is Type tt ? tt.Namespace : r.Member.DeclaringType!.Namespace;
            if (ns is "Microsoft.CSharp.RuntimeBinder" || (r.Member.DeclaringType ?? r.Member as Type)?.Name.StartsWith("CallSite", StringComparison.Ordinal) == true)
            {
                violations.Add(where + ": " + name + " (dynamic)");
            }
        }

        return violations;
    }

    /// <summary>
    /// The rule for <see cref="StationList"/>: it reads ids, names, kinds, keys and the format (card L8), never a stream or
    /// station URL. Genre, notes and the website reach it only through <see cref="StationText"/>, which applies the limits
    /// and the website rule.
    /// </summary>
    internal static List<string> StationListViolations(Type root)
    {
        var allowed = new Dictionary<Type, string[]>
        {
            [typeof(StationSource)] = ["get_Station", "get_Key", "get_Codec", "get_IsHls"],
            [typeof(StationDefinition)] = ["get_Id", "get_Kind", "get_BitrateKbps"],
            [typeof(StationResolver)] = [nameof(StationResolver.SubWaveStations), nameof(StationResolver.ListedStations)],
            [typeof(StationKey)] = ["get_StationId", "get_Origin"],
            [typeof(StationText)] = [nameof(StationText.Genre), nameof(StationText.Notes), nameof(StationText.Website)],
        };
        Type[] forbidden = [typeof(Plugin), typeof(PluginConfiguration), typeof(StationSources), typeof(StationUrls), typeof(Uri), typeof(UriBuilder), typeof(IServiceProvider), typeof(ServiceProviderServiceExtensions)];
        var violations = new List<string>();
        foreach (var r in IlReferences.Of(root))
        {
            var where = r.Owner + " (" + r.Method.Name + ")";
            if (r.Member is null)
            {
                violations.Add(where + ": unresolved token");
                continue;
            }

            var declaring = r.Member as Type ?? r.Member.DeclaringType!;
            foreach (var t in Expand(declaring).Where(forbidden.Contains))
            {
                violations.Add(where + ": touches " + t.Name);
            }

            if (r.Member is not Type && allowed.TryGetValue(declaring, out var members) && !members.Contains(r.Member.Name))
            {
                violations.Add(where + ": " + declaring.Name + "." + r.Member.Name + " is not one of " + string.Join(", ", members));
            }

            // The origin is compared with the applied cover's, in ImageTag only; it is never handed on.
            if (r.Member.DeclaringType == typeof(StationKey) && r.Member.Name == "get_Origin" && r.Owner != nameof(StationList.ImageTag))
            {
                violations.Add(where + ": StationKey.Origin outside ImageTag");
            }
        }

        return violations;
    }

    [Fact]
    public void The_controller_reaches_a_station_only_through_caller_station()
    {
        var violations = ControllerViolations(typeof(JellywaveWebController));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_station_list_reads_ids_names_and_keys_never_a_url()
    {
        var violations = StationListViolations(typeof(StationList));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_scan_reads_the_controller_s_state_machines_and_sees_its_openers()
    {
        // Control: the scan covers the async actions' state machines and sees the filter's station being opened.
        var references = IlReferences.Of(typeof(JellywaveWebController)).ToList();
        Assert.Contains(references, r => r.Owner == nameof(JellywaveWebController.LikeStatus) && r.Method.Name == "MoveNext" && r.Member?.Name == "GetLikeAsync");
        Assert.Contains(references, r => r.Owner == "CallerStation" && r.Member == typeof(StationSource));
        Assert.Contains(references, r => r.Owner == "BaseUrlOf" && r.Member?.Name == "get_BaseUrl");
        Assert.Contains(references, r => r.Owner == "CallerStation" && r.Member?.Name == "get_Items");
        Assert.Contains(references, r => r.Owner == "CallerId" && r.Member?.Name == "get_Items");
        Assert.Contains(references, r => r.Owner == "CallerChannel" && r.Member?.Name == "get_Items");
        Assert.All(references, r => Assert.NotNull(r.Member));
    }

    [Theory]
    [InlineData(nameof(Bypasses.FromServices))]
    [InlineData(nameof(Bypasses.FromStatic))]
    [InlineData(nameof(Bypasses.NewUri))]
    [InlineData(nameof(Bypasses.TryCreate))]
    [InlineData(nameof(Bypasses.Probe))]
    [InlineData(nameof(Bypasses.FromItems))]
    [InlineData(nameof(Bypasses.OriginText))]
    [InlineData(nameof(Bypasses.ByReflection))]
    [InlineData(nameof(Bypasses.InStateMachine))]
    [InlineData(nameof(Bypasses.InLambda))]
    [InlineData(nameof(Bypasses.OpenedElsewhere))]
    [InlineData(nameof(Bypasses.DefinitionUrl))]
    [InlineData(nameof(Bypasses.HandedBack))]
    [InlineData(nameof(Bypasses.TypeToken))]
    [InlineData(nameof(Bypasses.ItemsWrite))]
    [InlineData(nameof(Bypasses.ItemsRead))]
    [InlineData(nameof(Bypasses.ItemsFeature))]
    [InlineData("Helper.CallerId")]
    [InlineData(nameof(Bypasses.ItemsReplace))]
    [InlineData(nameof(Bypasses.ItemsFeatureSet))]
    [InlineData(nameof(Bypasses.ItemsFeatureNew))]
    [InlineData("CallerStation")]
    public void The_scan_catches_each_bypass(string method)
    {
        var violations = ControllerViolations(typeof(Bypasses));
        Assert.Contains(violations, v => v.StartsWith(method + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_station_list_scan_catches_a_url_read()
    {
        var violations = StationListViolations(typeof(LeakyList));
        Assert.Contains(violations, v => v.StartsWith(nameof(LeakyList.BaseOf) + " ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(nameof(LeakyList.BaseText) + " ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(nameof(LeakyList.OriginText) + " ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(nameof(LeakyList.RawWebsite) + " ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(nameof(LeakyList.ServerUrl) + " ", StringComparison.Ordinal));
    }

    /// <summary>The scanned types: the station page controller and the station list (with their nested compiler types).</summary>
    private static readonly Type[] ScannedRoots = [typeof(JellywaveWebController), typeof(StationList)];

    private static bool PluginOnly(Assembly assembly) => assembly == typeof(Plugin).Assembly;

    /// <summary>The controls' helpers live in this test assembly, so for them it counts as plugin code too.</summary>
    private static bool PluginOrControls(Assembly assembly) => PluginOnly(assembly) || assembly == typeof(StationRouteIlTests).Assembly;

    /// <summary>The allow-list the controls run against: what <see cref="HelperBypasses"/> may call.</summary>
    private static readonly (Type Type, string Why, string[] Reaches)[] ControlDataTypes =
    [
        (typeof(ControlData), "control data", []),
        (typeof(ControlInitData), "control: a property initializer reads the configuration", []),
        (typeof(ControlComputedData), "control: a computed getter nothing calls reads the configuration", []),
    ];

    private static readonly (string Member, string Why, string[] Reaches)[] ControlMembers =
    [
        ("ListedHelpers.Clean(String)", "control: reaches nothing", []),
        ("ListedHelpers.Deep(String)", "control: two private helpers down, reads the configuration", []),
        ("ListedHelpers.ThroughInterface(IControlSource)", "control: a plugin interface whose implementation reads the configuration", []),
        ("ListedHelpers.WriteItems(HttpContext, Object)", "control: writes the item bag", []),
        ("ListedHelpers.MakeKey()", "control: builds an object whose ToString reads the configuration", []),
        ("ListedHelpers.ThroughGenericInterface(IGenericOnly)", "control: only a generic class implements the interface", []),
        ("ListedHelpers.ThroughGenericOverride(ControlBase)", "control: only a generic class overrides the method", []),
        ("ListedHelpers.StructText()", "control: a struct's ToString reads the configuration", []),
        ("ListedHelpers.Show(T)", "control: ToString of whatever it is handed", []),
        ("ListedHelpers.Cached(StationDefinition)", "control: names only the notes, but keeps the last station in a static field", ["StationDefinition.get_Notes"]),
        ("ListedHelpers.NotesOf(StationDefinition)", "control: names only the notes, but reads the base URL", ["StationDefinition.get_Notes"]),
        ("ListedHelpers.ByCreateDelegate()", "control: calls a hidden method through Delegate.CreateDelegate", []),
        ("ListedHelpers.ByTypeList()", "control: lists the assembly's types", []),
    ];

    [Fact]
    public void The_station_routes_call_only_listed_plugin_members()
    {
        var violations = PluginCalls.Violations(ScannedRoots, PluginOnly, StationRouteAllowList.DataTypes, StationRouteAllowList.Members, reportStale: true);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_allow_list_walk_reads_below_the_listed_helpers()
    {
        // Control: the walk enters private helpers and their async state machines, and finds the resolver's configuration.
        var like = PluginCalls.Reach(typeof(StationClient).GetMethod(nameof(StationClient.GetLikeAsync))!, ScannedRoots, PluginOnly);
        Assert.Contains(like.Visited, v => v.StartsWith("<ReadReplyAsync>", StringComparison.Ordinal) && v.EndsWith(".MoveNext", StringComparison.Ordinal));
        Assert.Contains(like.Visited, v => v.StartsWith("<ReadBodyAsync>", StringComparison.Ordinal) && v.EndsWith(".MoveNext", StringComparison.Ordinal));
        Assert.Empty(like.Sinks);

        var listed = PluginCalls.Reach(typeof(StationResolver).GetMethod(nameof(StationResolver.ListedStations))!, ScannedRoots, PluginOnly);
        Assert.Contains("PluginConfiguration", listed.Sinks.Keys);
        Assert.Contains("StationSources", listed.Sinks.Keys);

        // The controller's calls into StationList are not walked: StationList is scanned by its own rule.
        var entries = PluginCalls.Reach(typeof(StationList).GetMethod(nameof(StationList.Entries))!, ScannedRoots, PluginOnly);
        Assert.Empty(entries.Visited);
    }

    [Fact]
    public void The_allow_list_accepts_auto_properties_only_by_their_il()
    {
        Assert.True(PluginCalls.IsAutoAccessor(typeof(ControlData).GetProperty(nameof(ControlData.Name))!.GetMethod!));
        Assert.True(PluginCalls.IsAutoAccessor(typeof(ControlData).GetProperty(nameof(ControlData.Name))!.SetMethod!));
        Assert.False(PluginCalls.IsAutoAccessor(typeof(ControlData).GetProperty(nameof(ControlData.Computed))!.GetMethod!));
        Assert.False(PluginCalls.IsAutoAccessor(typeof(StationClient).GetMethod(nameof(StationClient.IsValidId))!));
    }

    [Theory]
    [InlineData(nameof(HelperBypasses.StaticHelper), "calls UnlistedHelpers.Static(String), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.ExtensionHelper), "calls IlControlExtensions.Unlisted(String), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.InstanceHelper), "calls UnlistedInstance.Read(), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.UnlistedConstructor), "calls UnlistedInstance..ctor(), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.UnlistedField), "calls UnlistedHelpers.Shared, a plugin field not on the allow-list")]
    [InlineData(nameof(HelperBypasses.InLambda), "calls UnlistedHelpers.Static(String)")]
    [InlineData(nameof(HelperBypasses.InStateMachine), "calls UnlistedHelpers.Static(String)")]
    [InlineData(nameof(HelperBypasses.HandWrittenGetter), "calls ControlData.get_Computed(), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.StaticAutoProperty), "calls ControlData.get_Shared(), a plugin method not on the allow-list")]
    [InlineData(nameof(HelperBypasses.ListedReachesDeep), "ListedHelpers.Deep(String) reaches Plugin (")]
    [InlineData(nameof(HelperBypasses.ListedReachesDeep), "ListedHelpers.Deep(String) reaches PluginConfiguration (")]
    [InlineData(nameof(HelperBypasses.ListedReachesThroughInterface), "reaches PluginConfiguration (")]
    [InlineData(nameof(HelperBypasses.ListedWritesItems), "reaches HttpContext.Items (")]
    [InlineData(nameof(HelperBypasses.ListedConstructsCallback), "reaches PluginConfiguration (")]
    [InlineData(nameof(HelperBypasses.GenericInterface), "reaches PluginConfiguration (ListedHelpers.ThroughGenericInterface(IGenericOnly) > IGenericOnly.Read > GenericOnlyImpl`1.Read")]
    [InlineData(nameof(HelperBypasses.GenericOverride), "reaches PluginConfiguration (ListedHelpers.ThroughGenericOverride(ControlBase) > ControlBase.Read > GenericOverrideImpl`1.Read")]
    [InlineData(nameof(HelperBypasses.StructOverride), "ListedHelpers.StructText() reaches PluginConfiguration (")]
    [InlineData(nameof(HelperBypasses.GenericStruct), "ListedHelpers.Show(T) reaches PluginConfiguration (")]
    [InlineData(nameof(HelperBypasses.GenericStruct), "uses plugin type ControlStruct2 (a generic argument), which is not a listed data type")]
    [InlineData(nameof(HelperBypasses.StaticCache), "ListedHelpers.Cached(StationDefinition) reaches static field ListedHelpers._last (")]
    [InlineData(nameof(HelperBypasses.OtherField), "ListedHelpers.NotesOf(StationDefinition) reaches StationDefinition.get_BaseUrl (")]
    [InlineData(nameof(HelperBypasses.CreateDelegate), "ListedHelpers.ByCreateDelegate() reaches reflection (")]
    [InlineData(nameof(HelperBypasses.TypeList), "ListedHelpers.ByTypeList() reaches reflection (")]
    [InlineData(nameof(HelperBypasses.TypeToken), "uses plugin type UnlistedInstance (a type token), which is not a listed data type")]
    [InlineData(nameof(HelperBypasses.GenericArgument), "uses plugin type UnlistedInstance (a generic argument), which is not a listed data type")]
    [InlineData(nameof(HelperBypasses.ParameterType), "uses plugin type UnlistedInstance (a parameter type), which is not a listed data type")]
    [InlineData(nameof(HelperBypasses.ReturnType), "uses plugin type UnlistedInstance (a return type), which is not a listed data type")]
    public void The_allow_list_catches_each_helper_bypass(string method, string message)
    {
        var violations = PluginCalls.Violations([typeof(HelperBypasses)], PluginOrControls, ControlDataTypes, ControlMembers, reportStale: false);
        Assert.Contains(violations, v => v.StartsWith(method + " ", StringComparison.Ordinal) && v.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("data type ControlInitData: reaches PluginConfiguration (ControlInitData..ctor() > ")]
    [InlineData("data type ControlComputedData: reaches PluginConfiguration (ControlComputedData.get_Leak() > ")]
    public void The_allow_list_walks_each_data_type_whole(string message)
    {
        // R1 (S-b review): a listed data type's initializer, or a getter only the serializer calls, is read too.
        var violations = PluginCalls.Violations([typeof(HelperBypasses)], PluginOrControls, ControlDataTypes, ControlMembers, reportStale: false);
        Assert.Contains(violations, v => v.StartsWith(message, StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(HelperBypasses.Deserialized) + " ", StringComparison.Ordinal) || v.StartsWith(nameof(HelperBypasses.Serialized) + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allow_list_passes_a_clean_listed_helper_and_data()
    {
        var violations = PluginCalls.Violations([typeof(HelperBypasses)], PluginOrControls, ControlDataTypes, ControlMembers, reportStale: false);
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(HelperBypasses.ListedClean) + " ", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(HelperBypasses.DataRead) + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allow_list_reports_stale_lines()
    {
        (string Member, string Why, string[] Reaches)[] members = [.. ControlMembers.Where(m => m.Member != "ListedHelpers.Clean(String)"), ("ListedHelpers.Clean(String)", "control", ["Plugin"]), ("Nothing.Here()", "control", [])];
        var violations = PluginCalls.Violations([typeof(HelperBypasses)], PluginOrControls, [.. ControlDataTypes, (typeof(StationKey), "control", [])], members, reportStale: true);
        Assert.Contains("allow-list entry Nothing.Here() is used by no scanned type", violations);
        Assert.Contains("allow-list data type StationKey is used by no scanned type", violations);
        Assert.Contains("allow-list entry ListedHelpers.Clean(String) names Plugin but no longer reaches it", violations);

        var named = PluginCalls.Violations([typeof(HelperBypasses)], PluginOrControls, [(typeof(ControlData), "control", ["Plugin"])], ControlMembers, reportStale: true);
        Assert.Contains("allow-list data type ControlData names Plugin but no longer reaches it", named);
    }

    /// <summary>Every way around CallerStation the scan must see (test control only; never run).</summary>
    private sealed class Bypasses : ControllerBase
    {
        private readonly StationClient _client = null!;
        private readonly RequestReceipts _receipts = new();
        private readonly StationListLike _list = new();

        public object FromServices() => HttpContext.RequestServices.GetRequiredService<StationResolver>();

        public object FromStatic() => Plugin.Instance!.Configuration.Stations[0];

        public object NewUri() => new Uri("http://elsewhere.example/");

        public object? TryCreate() => Uri.TryCreate("http://elsewhere.example/", UriKind.Absolute, out var url) ? url : null;

        public object Probe() => _client.ProbeStreamAsync(null!, _ => false, CancellationToken.None);

        public object? FromItems() => HttpContext.Items["station"] as StationSource;

        public object? OriginText()
        {
            _receipts.Check("r", Guid.Empty, out var key);
            return key?.Origin;
        }

        public object? ByReflection() => typeof(Plugin).GetProperty("Instance")!.GetValue(null);

        public async Task<object> InStateMachine()
        {
            await Task.Yield();
            return StationSources.Primary(null!)!;
        }

        public Func<object> InLambda() => () => new StationResolver(null!, null!);

        public object? OpenedElsewhere(StationSource station) => station.Station;

        public object? DefinitionUrl(StationDefinition station) => station.BaseUrl;

        public object? HandedBack() => _list.BaseOf("live");

        public object TypeToken() => typeof(PluginConfiguration);

        // S-a: a station handed out as object and written into Items, where CallerStation would then find it.
        public void ItemsWrite(object station) => HttpContext.Items[StationRouteAttribute.StationItem] = station;

        public object? ItemsRead() => HttpContext.Items[StationRouteAttribute.StationItem];

        public object? ItemsFeature() => HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IItemsFeature>()?.Items;

        // R1: the whole bag replaced.
        public void ItemsReplace(object station) => HttpContext.Items = new Dictionary<object, object?> { ["x"] = station };

        // R4: a new item feature set through the feature collection.
        public void ItemsFeatureSet() => HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IItemsFeature>(null);

        public object ItemsFeatureNew() => new Microsoft.AspNetCore.Http.Features.ItemsFeature();

        // R2: an overload with an accessor's name is no exemption (it takes a parameter and returns nothing).
        public void CallerStation(object other) => HttpContext.Items["x"] = other;

        // An accessor's name on a type other than the scanned one is no exemption.
        private static class Helper
        {
            public static object? CallerId(Microsoft.AspNetCore.Http.HttpContext context) => context.Items["x"];
        }
    }

    /// <summary>Every way a plugin helper could be called around the allow-list (card S-b control only; never run).</summary>
    private sealed class HelperBypasses
    {
        private readonly UnlistedInstance _instance = null!;
        private readonly IControlSource _source = null!;
        private readonly IGenericOnly _generic = null!;
        private readonly ControlBase _base = null!;
        private readonly StationDefinition _definition = null!;

        public object StaticHelper() => UnlistedHelpers.Static("x");

        public object ExtensionHelper() => "x".Unlisted();

        public object InstanceHelper() => _instance.Read();

        public object UnlistedConstructor() => new UnlistedInstance();

        public object? UnlistedField() => UnlistedHelpers.Shared;

        public Func<object> InLambda() => () => UnlistedHelpers.Static("x");

        public async Task<object> InStateMachine()
        {
            await Task.Yield();
            return UnlistedHelpers.Static("x");
        }

        // A data type's hand-written getter and its static auto-property are not covered by the data type's line.
        public object HandWrittenGetter(ControlData data) => data.Computed;

        public object? StaticAutoProperty() => ControlData.Shared;

        public object? ListedReachesDeep() => ListedHelpers.Deep("x");

        public object? ListedReachesThroughInterface() => ListedHelpers.ThroughInterface(_source);

        public void ListedWritesItems(Microsoft.AspNetCore.Http.HttpContext context) => ListedHelpers.WriteItems(context, "x");

        public object ListedConstructsCallback() => ListedHelpers.MakeKey();

        public object ListedClean() => ListedHelpers.Clean("x");

        public object? DataRead() => new ControlData { Name = "x" }.Name;

        // R2: interface and virtual dispatch reach generic implementations.
        public object? GenericInterface() => ListedHelpers.ThroughGenericInterface(_generic);

        public object? GenericOverride() => ListedHelpers.ThroughGenericOverride(_base);

        // S1: a value type's override runs wherever the value goes.
        public object StructOverride() => ListedHelpers.StructText();

        public object? GenericStruct() => ListedHelpers.Show(default(ControlStruct2));

        // S2: a static cache of stations, and a station member the line does not name.
        public object? StaticCache() => ListedHelpers.Cached(_definition);

        public object? OtherField() => ListedHelpers.NotesOf(_definition);

        // S3: reflection by other names.
        public object? CreateDelegate() => ListedHelpers.ByCreateDelegate();

        public object TypeList() => ListedHelpers.ByTypeList();

        // S4: plugin types the framework builds or reads for the scanned code.
        public object TypeToken() => typeof(UnlistedInstance);

        public object? GenericArgument() => System.Text.Json.JsonSerializer.Deserialize<UnlistedInstance>("{}");

        public int ParameterType(UnlistedInstance value) => value.GetHashCode();

        public UnlistedInstance ReturnType() => null!;

        // R1: listed data types the serializer builds or reads; the data types' own walk sees what runs in them.
        public object? Deserialized() => System.Text.Json.JsonSerializer.Deserialize<ControlInitData>("{}")?.Leak;

        public object Serialized() => System.Text.Json.JsonSerializer.Serialize(new ControlComputedData());
    }

    private static class UnlistedHelpers
    {
        public static readonly object Shared = new();

        public static string Static(string value) => value;
    }

    private sealed class UnlistedInstance
    {
        public string Read() => "x";
    }

    private sealed class ControlData
    {
        public static string? Shared { get; set; }

        public string? Name { get; set; }

        public string Computed => Name ?? string.Empty;
    }

    private static class ListedHelpers
    {
        public static string Clean(string value) => value.Trim();

        public static object? Deep(string value) => Middle(value);

        public static object? ThroughInterface(IControlSource source) => source.Read();

        public static void WriteItems(Microsoft.AspNetCore.Http.HttpContext context, object value) => context.Items["x"] = value;

        public static object MakeKey() => new SneakyKey();

        private static object? Middle(string value) => Bottom(value);

        private static StationDefinition? _last;

        public static object? ThroughGenericInterface(IGenericOnly source) => source.Read();

        public static object? ThroughGenericOverride(ControlBase source) => source.Read();

        public static object StructText() => default(ControlStruct).ToString();

        public static object? Show<T>(T value) => value?.ToString();

        public static object? Cached(StationDefinition station)
        {
            var other = _last ?? station;
            _last = station;
            return other.Notes;
        }

        public static object? NotesOf(StationDefinition station) => station.BaseUrl;

        public static object? ByCreateDelegate() => ((Func<object?>)Delegate.CreateDelegate(typeof(Func<object?>), typeof(HiddenConfig), "Read"))();

        public static object ByTypeList() => typeof(ListedHelpers).Assembly.GetExportedTypes();

        private static object? Bottom(string value) => value.Length > 0 ? Plugin.Instance?.Configuration : null;
    }

    private interface IControlSource
    {
        object? Read();
    }

    private sealed class ConfigSource : IControlSource
    {
        public object? Read() => Plugin.Instance?.Configuration;
    }

    private sealed class ControlInitData
    {
        public string? Leak { get; set; } = Plugin.Instance?.Configuration.LibraryName;
    }

    private sealed class ControlComputedData
    {
        public string? Name { get; set; }

        public string Leak => Plugin.Instance?.Configuration.LibraryName ?? string.Empty;
    }

    private interface IGenericOnly
    {
        object? Read();
    }

    private sealed class GenericOnlyImpl<T> : IGenericOnly
    {
        public object? Read() => Plugin.Instance?.Configuration;
    }

    private abstract class ControlBase
    {
        public abstract object? Read();
    }

    private sealed class GenericOverrideImpl<T> : ControlBase
    {
        public override object? Read() => Plugin.Instance?.Configuration;
    }

    private struct ControlStruct
    {
        public override readonly string ToString() => Plugin.Instance?.Configuration.LibraryName ?? string.Empty;
    }

    private struct ControlStruct2
    {
        public override readonly string ToString() => Plugin.Instance?.Configuration.LibraryName ?? string.Empty;
    }

    private static class HiddenConfig
    {
        public static object? Read() => Plugin.Instance?.Configuration;
    }

    /// <summary>Built by a listed helper; the framework may call its ToString, which reads the configuration.</summary>
    private sealed class SneakyKey
    {
        public override string ToString() => Plugin.Instance?.Configuration.LibraryName ?? string.Empty;
    }

    /// <summary>A dependency that hands out a URL (test control only).</summary>
    private sealed class StationListLike
    {
        public Uri? BaseOf(string id) => id.Length == 0 ? null : null;
    }

    /// <summary>A station list that reads URLs (test control only; never run).</summary>
    private sealed class LeakyList
    {
        private readonly StationResolver _stations = null!;

        public Uri? BaseOf(string id) => _stations.SubWaveStations().FirstOrDefault(s => s.Station.Id == id)?.BaseUrl;

        public string? BaseText(string id) => _stations.SubWaveStations().FirstOrDefault(s => s.Station.Id == id)?.Station.BaseUrl;

        public string? OriginText(StationKey key) => key.Origin;

        public string? RawWebsite(string id) => _stations.ListedStations().FirstOrDefault(s => s.Station.Id == id)?.Station.Website;

        public Uri? ServerUrl(string id) => _stations.ListedStations().FirstOrDefault(s => s.Station.Id == id)?.ServerUrl;
    }
}
