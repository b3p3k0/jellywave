using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card S-b: the allow-list half of the station route IL rule. What it checks, for each scanned type (the station page
/// controller and <see cref="StationList"/>, with their compiler-generated nested types):
/// <list type="number">
/// <item>Every plugin method, constructor or field the scanned IL references is on <see cref="StationRouteAllowList"/>:
/// a member line, or an instance auto-property accessor (proven by its IL) or parameterless constructor of a listed data
/// type.</item>
/// <item>Every plugin type the framework builds or reads for the scanned code is a listed data type: one that appears
/// as a type token (<c>typeof</c>, <c>isinst</c>, <c>box</c>), as a generic argument (<c>Deserialize&lt;X&gt;</c>,
/// <c>ActionResult&lt;X&gt;</c>), or as a parameter or return type of a scanned method (model binding, serialization).
/// Constructors are exempt: their parameters are the DI services <c>StationRouteCoverageTests</c> walks.</item>
/// <item>Each listed member is followed through the plugin's code, fully transitively, and may reach a sink (below)
/// only where its line names it; <c>Type.*</c> names every member of a station type.</item>
/// <item>Each listed data type is walked whole (every declared constructor, the static constructor, every method and
/// accessor, also of its plugin base types), so a property initializer or a getter only the serializer calls is seen;
/// it may reach its own type's members and only the sinks its line names.</item>
/// <item>For the real list: a line nothing uses, or a named sink no longer reached, fails.</item>
/// </list>
/// <para>
/// Why the walk is full and the list is one level: the list is the review gate (each line is a decision someone reads),
/// so it holds only what the scanned types use themselves; listing the whole closure would add every HTTP and parsing
/// helper below <see cref="StationClient"/> and nobody would read it. A helper's danger is that it reaches the
/// configuration, the resolver, a station, a service locator, the item bag or reflection, at any depth: one level would
/// let a listed helper call an unlisted one that reads <c>Plugin.Instance.Configuration</c>. The walk follows calls and
/// method references (<c>ldftn</c>, <c>ldvirtftn</c>) into plugin code; the methods of compiler types (lambdas, closures,
/// async state machines, iterators) it touches; static constructors of the plugin types it touches; every plugin
/// implementation or override of a plugin interface or virtual method it calls (generic types included); the virtual
/// methods of each plugin type it constructs; and the constructors, virtual methods and property accessors of each plugin
/// type it hands to other code (a type token or generic argument, which a serializer or the framework may build and
/// read). A plugin value type counts as handed whenever it is touched (<c>initobj</c>, <c>box</c>, <c>constrained.</c>).
/// </para>
/// <para>
/// Sinks: <c>Plugin</c>, <c>PluginConfiguration</c>, <c>StationResolver</c>, <c>StationSources</c>, a service locator,
/// the item bag or feature, <c>ProbeStreamAsync</c>, reflection (<c>MemberInfo</c> and its kinds, type and assembly
/// enumeration, <c>Delegate.CreateDelegate</c>, <c>RunClassConstructor</c>, compiled expressions) and dynamic code; a
/// member of <c>StationDefinition</c> or <c>StationSource</c>, labelled per member (<c>StationDefinition.get_Notes</c>),
/// or either type appearing as a token, result or generic argument (labelled by the type); and a static field whose type
/// is or holds a sink type (a cache that could hand back another station).
/// </para>
/// <para>
/// What the walk cannot see: a delegate invoked through a non-plugin delegate type whose target was made outside the
/// walk (a <c>Func&lt;object&gt;</c> handed in by DI); plugin code the framework builds and calls without a token in the
/// walked code (an HTTP handler or JSON converter registered at startup, or named by an attribute such as
/// <c>[JsonConverter]</c>, since attributes are not IL); a framework virtual call on a plugin object the walk neither
/// constructs nor hands on (framework virtuals are not fanned out to every plugin override); a station value written into
/// shared state of a non-sink type by code outside the walk and read back as a plain value; and reflection through names
/// the sinks do not cover. The behaviour tests (every route against unlisted ids, the loopback pipeline) still stand
/// behind it.
/// </para>
/// </summary>
internal static class PluginCalls
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Types that are, hold or hand out a station's configuration, or locate services: reaching one is a sink.</summary>
    private static readonly Type[] SinkTypes =
    [
        typeof(Plugin), typeof(PluginConfiguration), typeof(StationResolver), typeof(StationSources), typeof(StationDefinition),
        typeof(StationSource), typeof(IServiceProvider), typeof(ServiceProviderServiceExtensions), typeof(ActivatorUtilities),
        typeof(Activator), typeof(Microsoft.AspNetCore.Http.Features.IItemsFeature), typeof(Microsoft.AspNetCore.Http.Features.ItemsFeature),
        typeof(MemberInfo), typeof(MethodBase), typeof(MethodInfo), typeof(ConstructorInfo), typeof(PropertyInfo), typeof(FieldInfo),
        typeof(EventInfo)
    ];

    /// <summary>The station types: a member of one is labelled per member, so a line can name exactly what it reads.</summary>
    private static readonly Type[] StationTypes = [typeof(StationDefinition), typeof(StationSource)];

    /// <summary>Members that are sinks by name (declaring type or a base of it, member name, sink label).</summary>
    private static readonly (Type Type, string Name, string Sink)[] SinkMembers =
    [
        (typeof(Microsoft.AspNetCore.Http.HttpContext), "get_RequestServices", "RequestServices"),
        (typeof(Microsoft.AspNetCore.Http.HttpContext), "get_Items", "HttpContext.Items"),
        (typeof(Microsoft.AspNetCore.Http.HttpContext), "set_Items", "HttpContext.Items"),
        (typeof(StationClient), nameof(StationClient.ProbeStreamAsync), "ProbeStreamAsync"),
        (typeof(Type), nameof(Type.GetType), "reflection"),
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
        (typeof(Type), nameof(Type.GetConstructors), "reflection"),
        (typeof(Type), nameof(Type.GetNestedTypes), "reflection"),
        (typeof(Assembly), nameof(Assembly.GetType), "reflection"),
        (typeof(Assembly), nameof(Assembly.GetTypes), "reflection"),
        (typeof(Assembly), nameof(Assembly.GetExportedTypes), "reflection"),
        (typeof(Assembly), "get_DefinedTypes", "reflection"),
        (typeof(Assembly), "get_ExportedTypes", "reflection"),
        (typeof(Assembly), nameof(Assembly.CreateInstance), "reflection"),
        (typeof(Module), nameof(Module.GetTypes), "reflection"),
        (typeof(Module), nameof(Module.ResolveMethod), "reflection"),
        (typeof(Module), nameof(Module.ResolveMember), "reflection"),
        (typeof(Delegate), nameof(Delegate.CreateDelegate), "reflection"),
        (typeof(RuntimeHelpers), nameof(RuntimeHelpers.RunClassConstructor), "reflection"),
        (typeof(System.Linq.Expressions.LambdaExpression), nameof(System.Linq.Expressions.LambdaExpression.Compile), "reflection"),
    ];

    /// <summary>How a walk met a plugin type, which decides how much of it the walk reads.</summary>
    private enum Touch
    {
        /// <summary>Declares a member the code uses: its static constructor (and all of it, for a compiler type).</summary>
        Declaring,

        /// <summary>Constructed by the code: also its virtual methods, which the framework may call back.</summary>
        Constructed,

        /// <summary>Handed to other code as a type token or generic argument: also its constructors and accessors.</summary>
        Handed,
    }

    /// <summary>
    /// What the scanned types' plugin use breaks (see the class summary). Each violation starts with the scanned method's
    /// name, or with "data type" for a data type's own walk, or with "allow-list" for a stale line.
    /// </summary>
    /// <param name="roots">The scanned types; a call into one of them is not walked (each is scanned on its own).</param>
    /// <param name="own">Which assemblies are plugin code (the controls add the test assembly).</param>
    /// <param name="dataTypes">Listed data types: the type, the reason, the sinks outside its own type it may reach.</param>
    /// <param name="members">Listed members: key, reason, the sinks the reason covers.</param>
    /// <param name="reportStale">Whether a line or a named sink nothing uses is a violation (the real list only).</param>
    /// <returns>The violations.</returns>
    internal static List<string> Violations(
        Type[] roots,
        Func<Assembly, bool> own,
        (Type Type, string Why, string[] Reaches)[] dataTypes,
        (string Member, string Why, string[] Reaches)[] members,
        bool reportStale)
    {
        var violations = new List<string>();
        var listed = members.ToDictionary(m => m.Member, m => m.Reaches, StringComparer.Ordinal);
        var data = dataTypes.Select(d => d.Type).ToHashSet();
        var usedMembers = new HashSet<string>(StringComparer.Ordinal);
        var usedData = new HashSet<Type>();
        var reached = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var walks = new Dictionary<MemberInfo, Dictionary<string, string>>();

        bool Foreign(Type type) => !own(type.Assembly) || roots.Any(x => InFamily(type, x)) || IsCompilerType(type);

        void NeedData(Type type, string where, string how)
        {
            type = Open(type);
            if (Foreign(type))
            {
                return;
            }

            if (data.Contains(type))
            {
                usedData.Add(type);
                return;
            }

            violations.Add(where + ": uses plugin type " + NameOf(type) + " (" + how + "), which is not a listed data type (StationRouteAllowList)");
        }

        foreach (var root in roots)
        {
            // What model binding builds and the serializer reads: the scanned methods' parameter and return types.
            foreach (var method in IlReferences.Family(root).SelectMany(t => t.GetMethods(Declared)))
            {
                var where = method.Name + " (" + root.Name + ", " + method.DeclaringType!.Name + "." + method.Name + ")";
                foreach (var type in method.GetParameters().Select(p => p.ParameterType).SelectMany(Expand))
                {
                    NeedData(type, where, "a parameter type");
                }

                foreach (var type in Expand(method.ReturnType))
                {
                    NeedData(type, where, "a return type");
                }
            }

            foreach (var r in IlReferences.Of(root))
            {
                if (r.Member is null)
                {
                    continue;   // the deny rules report an unresolved token
                }

                var where = r.Owner + " (" + root.Name + ", " + r.Method.DeclaringType!.Name + "." + r.Method.Name + ")";
                foreach (var (handed, how) in HandedTypes(r.Member))
                {
                    NeedData(handed, where, how);
                }

                if (r.Member is Type || r.Member.DeclaringType is not { } declaring || !own(declaring.Assembly)
                    || roots.Any(x => InFamily(declaring, x) && InFamily(r.Method.DeclaringType, x)))
                {
                    continue;
                }

                var member = Definition(r.Member);
                var type = Open(member.DeclaringType!);
                if (data.Contains(type) && IsDataMember(member))
                {
                    usedData.Add(type);
                    continue;   // the data type's own walk below reads the member
                }

                var key = KeyOf(member);
                if (!listed.TryGetValue(key, out var reaches))
                {
                    violations.Add(where + ": calls " + key + ", a plugin " + (member is FieldInfo ? "field" : "method") + " not on the allow-list (StationRouteAllowList)");
                    continue;
                }

                usedMembers.Add(key);

                // The member as referenced (a generic method with its type arguments), so a handed value type is seen.
                if (!walks.TryGetValue(r.Member, out var sinks))
                {
                    sinks = Reach(r.Member, roots, own).Sinks;
                    walks[r.Member] = sinks;
                }

                if (!reached.TryGetValue(key, out var all))
                {
                    reached[key] = all = new HashSet<string>(StringComparer.Ordinal);
                }

                all.UnionWith(sinks.Keys);
                foreach (var (sink, path) in sinks.Where(s => !Named(reaches, s.Key)))
                {
                    violations.Add(where + ": " + key + " reaches " + sink + " (" + path + "), which its allow-list entry does not name");
                }
            }
        }

        // Each listed data type whole: what the framework may run on it (constructors, initializers, every accessor).
        foreach (var (type, _, reaches) in dataTypes)
        {
            var sinks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var method in DeclaredCode(type, own))
            {
                foreach (var (sink, path) in Reach(method, roots, own).Sinks)
                {
                    sinks.TryAdd(sink, path);
                }
            }

            var mine = NameOf(Open(type));
            foreach (var (sink, path) in sinks.Where(s => !Named([mine + ".*", .. reaches], s.Key)))
            {
                violations.Add("data type " + mine + ": reaches " + sink + " (" + path + "), which its data-type line does not name");
            }

            if (reportStale)
            {
                violations.AddRange(reaches.Where(x => !sinks.Keys.Any(s => Named([x], s))).Select(x => "allow-list data type " + mine + " names " + x + " but no longer reaches it"));
            }
        }

        if (reportStale)
        {
            violations.AddRange(members.Where(m => !usedMembers.Contains(m.Member)).Select(m => "allow-list entry " + m.Member + " is used by no scanned type"));
            violations.AddRange(dataTypes.Where(d => !usedData.Contains(Open(d.Type))).Select(d => "allow-list data type " + NameOf(Open(d.Type)) + " is used by no scanned type"));
            foreach (var (key, _, reaches) in members.Where(m => usedMembers.Contains(m.Member)))
            {
                var sinks = reached.TryGetValue(key, out var s) ? s : [];
                violations.AddRange(reaches.Where(x => !sinks.Any(k => Named([x], k))).Select(x => "allow-list entry " + key + " names " + x + " but no longer reaches it"));
            }
        }

        return violations.Distinct().ToList();
    }

    /// <summary>The allow-list key of a member: <c>Type.Name(ParameterTypes)</c>, generic types written out.</summary>
    internal static string KeyOf(MemberInfo member)
    {
        var owner = NameOf(Open(member.DeclaringType!));
        return member is MethodBase method
            ? owner + "." + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => NameOf(p.ParameterType))) + ")"
            : owner + "." + member.Name;
    }

    /// <summary>
    /// An auto-property accessor: compiler-generated, special-name, and its IL references exactly one field, its own
    /// property's backing field on its own type. Its body can reach nothing else.
    /// </summary>
    internal static bool IsAutoAccessor(MethodBase method)
    {
        if (method is not MethodInfo { IsSpecialName: true } m || !m.IsDefined(typeof(CompilerGeneratedAttribute))
            || !(m.Name.StartsWith("get_", StringComparison.Ordinal) || m.Name.StartsWith("set_", StringComparison.Ordinal)))
        {
            return false;
        }

        var tokens = IlReferences.InMethod(m).ToList();
        return tokens.Count == 1
            && tokens[0] is FieldInfo field
            && Open(field.DeclaringType!) == Open(m.DeclaringType!)
            && field.IsStatic == m.IsStatic
            && field.Name == "<" + m.Name[4..] + ">k__BackingField";
    }

    /// <summary>
    /// Follows a member through plugin code: each sink it reaches with the first path that reached it, and every plugin
    /// method the walk read (<c>Type.Method</c>). Stops at the scanned roots (scanned on their own) and at non-plugin code.
    /// </summary>
    internal static (Dictionary<string, string> Sinks, List<string> Visited) Reach(MemberInfo start, Type[] roots, Func<Assembly, bool> own)
    {
        var visited = new List<string>();
        var sinks = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<(Module, int, string)>();
        var touched = new Dictionary<Type, Touch>();
        var queue = new Queue<(MethodBase Method, string Path)>();

        void Enqueue(MethodBase method, string path)
        {
            var type = method.DeclaringType;

            // A generic method is walked once per set of type arguments, so the arguments a call hands in are seen.
            var arguments = method is MethodInfo { IsGenericMethod: true } g ? string.Join(",", g.GetGenericArguments().Select(a => a.FullName ?? a.Name)) : string.Empty;
            if (type is null || !own(type.Assembly) || roots.Any(r => InFamily(type, r)) || !seen.Add((method.Module, method.MetadataToken, arguments)))
            {
                return;
            }

            queue.Enqueue((method, path));
        }

        void TouchType(Type type, string path, Touch how)
        {
            type = Open(type);
            if (!own(type.Assembly) || roots.Any(r => InFamily(type, r)))
            {
                return;
            }

            var compiler = IsCompilerType(type);
            if (type.IsValueType && !type.IsEnum)
            {
                how = Touch.Handed;   // a value type exists wherever it is touched; boxed, its overrides run anywhere
            }

            if (touched.TryGetValue(type, out var before) && before >= how)
            {
                return;
            }

            touched[type] = how;
            if (type.TypeInitializer is { } cctor)
            {
                Enqueue(cctor, path + " > " + type.Name + "..cctor");
            }

            // Compiler types (state machines, closures, iterators) run whatever their methods hold. A constructed plugin type's
            // virtual methods may be called back by the framework (ToString, Equals, an interface it implements); a handed one
            // may also be built and read by it (a serializer runs its constructors and accessors).
            IEnumerable<MethodBase> methods = how switch
            {
                _ when compiler => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)),
                Touch.Constructed => type.GetMethods(Declared).Where(m => m.IsVirtual),
                Touch.Handed => type.GetMethods(Declared).Where(m => m.IsVirtual || (m.IsSpecialName && !m.IsStatic)).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)),
                _ => [],
            };

            foreach (var m in methods)
            {
                Enqueue(m, path + " > " + type.Name + "." + m.Name);
            }
        }

        switch (start)
        {
            case MethodBase method:
                Enqueue(method, KeyOf(Definition(start)));
                foreach (var target in Dispatch(method, own))
                {
                    Enqueue(target, KeyOf(Definition(start)) + " > " + target.DeclaringType!.Name + "." + target.Name);
                }

                foreach (var (type, _) in HandedTypes(method))
                {
                    TouchType(type, KeyOf(Definition(start)), Touch.Handed);
                }

                break;
            case FieldInfo field:
                TouchType(field.DeclaringType!, KeyOf(start), Touch.Declaring);
                foreach (var sink in SinksOf(field))
                {
                    sinks.TryAdd(sink, KeyOf(start));
                }

                break;
        }

        while (queue.TryDequeue(out var item))
        {
            var (method, path) = item;
            visited.Add(method.DeclaringType!.Name + "." + method.Name);
            TouchType(method.DeclaringType!, path, method is ConstructorInfo { IsStatic: false } ? Touch.Constructed : Touch.Declaring);
            foreach (var member in IlReferences.InMethod(method))
            {
                if (member is null)
                {
                    sinks.TryAdd("an unresolved token", path);
                    continue;
                }

                var label = member is Type ? member.Name : member.DeclaringType!.Name + "." + member.Name;
                var next = path + " > " + label;
                foreach (var sink in SinksOf(member))
                {
                    sinks.TryAdd(sink, next);
                }

                if (member.DeclaringType is { } declaring && member is not Type)
                {
                    TouchType(declaring, next, member is ConstructorInfo { IsStatic: false } ? Touch.Constructed : Touch.Declaring);
                }

                foreach (var (type, _) in HandedTypes(member))
                {
                    TouchType(type, next, Touch.Handed);
                }

                if (member is MethodBase called)
                {
                    Enqueue(called, next);
                    foreach (var target in Dispatch(called, own))
                    {
                        Enqueue(target, next + " > " + target.DeclaringType!.Name + "." + target.Name);
                    }
                }
            }
        }

        return (sinks, visited);
    }

    /// <summary>A data-type member covered by its type's line: an instance auto-property accessor, or the parameterless constructor.</summary>
    private static bool IsDataMember(MemberInfo member)
        => member switch
        {
            MethodInfo m => !m.IsStatic && IsAutoAccessor(m),
            ConstructorInfo c => !c.IsStatic && c.GetParameters().Length == 0,
            _ => false
        };

    /// <summary>Every method, accessor and constructor (the static one too) of a type and of its plugin base types.</summary>
    private static IEnumerable<MethodBase> DeclaredCode(Type type, Func<Assembly, bool> own)
    {
        for (var t = type; t is not null && own(t.Assembly); t = t.BaseType is { } b ? Open(b) : null)
        {
            foreach (var m in t.GetMethods(Declared).Cast<MethodBase>().Concat(t.GetConstructors(Declared)))
            {
                yield return m;
            }
        }
    }

    /// <summary>Whether a line's named sinks cover a sink: by name, or <c>Type.*</c> for the type and all its members.</summary>
    private static bool Named(IEnumerable<string> reaches, string sink)
        => reaches.Any(r => r == sink
            || (r.EndsWith(".*", StringComparison.Ordinal) && (sink == r[..^2] || sink.StartsWith(r[..^1], StringComparison.Ordinal))));

    /// <summary>The sinks one referenced member or type is.</summary>
    private static IEnumerable<string> SinksOf(MemberInfo member)
    {
        // A member of a station type is labelled by itself, not by its type; a backing field is reached only through its
        // own accessor, which carries the label.
        var station = member is not Type && member.DeclaringType is { } d && StationTypes.Contains(Open(d));
        var backing = member is FieldInfo && member.Name.EndsWith(">k__BackingField", StringComparison.Ordinal);
        var types = new List<Type>();
        if (member is Type token)
        {
            types.AddRange(Expand(token));
        }
        else
        {
            // A station type's own member is labelled by itself below; its declaring type does not count again.
            types.AddRange(station ? Expand(member.DeclaringType!).Skip(1) : Expand(member.DeclaringType!));
            if (member is MethodInfo { IsGenericMethod: true } generic)
            {
                types.AddRange(generic.GetGenericArguments().SelectMany(Expand));
            }

            if (Result(member) is { } result)
            {
                types.AddRange(Expand(result));
            }
        }

        if (station && !backing)
        {
            yield return Open(member.DeclaringType!).Name + "." + member.Name;
        }

        foreach (var t in types.Where(SinkTypes.Contains).Distinct())
        {
            yield return t.Name;
        }

        // A compiler type's static field is a cached lambda or its singleton, set only by compiler code.
        if (member is FieldInfo { IsStatic: true } field && !IsCompilerType(field.DeclaringType!) && Expand(field.FieldType).Any(SinkTypes.Contains))
        {
            yield return "static field " + Open(field.DeclaringType!).Name + "." + field.Name;
        }

        foreach (var (type, name, sink) in SinkMembers)
        {
            if (member.Name == name && member.DeclaringType is { } declaring && type.IsAssignableFrom(declaring))
            {
                yield return sink;
            }
        }

        var ns = (member as Type ?? member.DeclaringType)?.Namespace;
        if (ns is "Microsoft.CSharp.RuntimeBinder" || (member.DeclaringType ?? member as Type)?.Name.StartsWith("CallSite", StringComparison.Ordinal) == true)
        {
            yield return "dynamic";
        }
    }

    /// <summary>
    /// The types a reference hands to other code: a type token and its arguments, the type arguments of a member's
    /// declaring type, and a generic method's type arguments.
    /// </summary>
    private static IEnumerable<(Type Type, string How)> HandedTypes(MemberInfo member)
    {
        if (member is Type token)
        {
            return Expand(token).Select(t => (t, "a type token"));
        }

        var declaring = member.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments().SelectMany(Expand) : [];
        var method = member is MethodInfo { IsGenericMethod: true } g ? g.GetGenericArguments().SelectMany(Expand) : [];
        return declaring.Concat(method).Select(t => (t, "a generic argument"));
    }

    /// <summary>
    /// The plugin implementations a call to a plugin interface or virtual method may dispatch to, generic types included.
    /// A call to a framework virtual (<c>Dispose</c>, <c>ToString</c>) is not fanned out to every plugin type; the overrides
    /// of the plugin types the walk constructs or hands on are walked instead.
    /// </summary>
    private static IEnumerable<MethodBase> Dispatch(MethodBase called, Func<Assembly, bool> own)
    {
        if (called is not MethodInfo { IsVirtual: true } method || method.IsFinal || called.DeclaringType is not { } declaring || !own(declaring.Assembly))
        {
            yield break;
        }

        var types = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && own(a)).SelectMany(a => a.GetTypes()).Where(t => !t.IsInterface);
        if (declaring.IsInterface)
        {
            foreach (var type in types)
            {
                foreach (var iface in type.GetInterfaces().Where(i => Open(i) == Open(declaring)))
                {
                    var map = type.GetInterfaceMap(iface);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].MetadataToken == method.MetadataToken && map.InterfaceMethods[i].Module == method.Module)
                        {
                            yield return map.TargetMethods[i];
                        }
                    }
                }
            }

            yield break;
        }

        var root = method.GetBaseDefinition();
        foreach (var type in types)
        {
            foreach (var m in type.GetMethods(Declared).Where(m => m.IsVirtual))
            {
                var baseDefinition = m.GetBaseDefinition();
                if (baseDefinition.MetadataToken == root.MetadataToken && baseDefinition.Module == root.Module)
                {
                    yield return m;
                }
            }
        }
    }

    private static bool IsCompilerType(Type type) => type.IsDefined(typeof(CompilerGeneratedAttribute)) || type.Name.StartsWith('<');

    private static Type? Result(MemberInfo member) => member switch
    {
        MethodInfo m => m.ReturnType,
        FieldInfo f => f.FieldType,
        _ => null
    };

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return Open(type);
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

    private static Type Open(Type type) => type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;

    /// <summary>The member as declared (an open generic type's, a generic method's definition), so keys do not depend on type arguments.</summary>
    private static MemberInfo Definition(MemberInfo member)
        => member.DeclaringType is { IsGenericType: true } || member is MethodInfo { IsGenericMethod: true }
            ? member.Module.ResolveMember(member.MetadataToken)!
            : member;

    private static string NameOf(Type type)
    {
        if (type.IsByRef)
        {
            return NameOf(type.GetElementType()!) + "&";
        }

        if (type.IsArray)
        {
            return NameOf(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return (tick < 0 ? name : name[..tick]) + "<" + string.Join(", ", type.GetGenericArguments().Select(NameOf)) + ">";
    }

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
}
