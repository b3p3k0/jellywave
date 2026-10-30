using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Reads the IL of a type, its nested types (the compiler's async state machines, lambda closures and display classes)
/// and every method and constructor in them, and resolves each member and type token the IL references. Used to check
/// what the station page controller can reach (card L5). A token that does not resolve is reported as such, never skipped.
/// </summary>
internal static class IlReferences
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static readonly Regex CompilerName = new("^<([^>]+)>", RegexOptions.Compiled);

    /// <summary>
    /// The types whose IL belongs to <paramref name="root"/>: itself and its nested types, recursively.
    /// </summary>
    public static IEnumerable<Type> Family(Type root)
        => new[] { root }.Concat(root.GetNestedTypes(Declared).SelectMany(Family));

    /// <summary>
    /// Every reference in the IL of <paramref name="root"/> and its nested types: the method it is in, the name of the
    /// source method that method belongs to (an async state machine's <c>MoveNext</c> or a lambda belongs to the method
    /// that declared it), and the member or type the token resolves to.
    /// </summary>
    public static IEnumerable<IlReference> Of(Type root)
    {
        foreach (var type in Family(root))
        {
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                var owner = OwnerOf(method, root);
                foreach (var (member, token) in Resolved(method))
                {
                    yield return new IlReference(method, owner, member, token);
                }
            }
        }
    }

    /// <summary>
    /// The members and types one method's IL references, null for a token that does not resolve (card S-b: the walk
    /// through the plugin helpers the station routes call).
    /// </summary>
    public static IEnumerable<MemberInfo?> InMethod(MethodBase method) => Resolved(method).Select(r => r.Member);

    private static IEnumerable<(MemberInfo? Member, int Token)> Resolved(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        foreach (var token in Tokens(il))
        {
            MemberInfo? member;
            try
            {
                member = method.Module.ResolveMember(
                    token,
                    method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
                member = null;
            }

            yield return (member, token);
        }
    }

    /// <summary>
    /// The source method a compiled method belongs to: its own name in <paramref name="root"/>; for a lambda
    /// (<c>&lt;Feed&gt;b__3_0</c>) or a state machine type (<c>&lt;LikeStatus&gt;d__12</c>) the name in angle brackets.
    /// </summary>
    private static string OwnerOf(MethodBase method, Type root)
    {
        var match = CompilerName.Match(method.Name);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        for (var type = method.DeclaringType; type is not null && type != root; type = type.DeclaringType)
        {
            match = CompilerName.Match(type.Name);
            if (match.Success && !type.Name.StartsWith("<>", StringComparison.Ordinal))
            {
                return match.Groups[1].Value;
            }
        }

        return method.DeclaringType == root ? method.Name : method.DeclaringType!.Name + "." + method.Name;
    }

    /// <summary>The member, type and token operands in a method body.</summary>
    private static IEnumerable<int> Tokens(byte[] il)
    {
        var at = 0;
        while (at < il.Length)
        {
            short value = il[at++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[at++]));
            }

            if (!Codes.TryGetValue(value, out var code))
            {
                throw new InvalidOperationException("Unknown opcode 0x" + value.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " at " + (at - 1));
            }

            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    yield return BitConverter.ToInt32(il, at);
                    at += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    at += 1;
                    break;
                case OperandType.InlineVar:
                    at += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    at += 8;
                    break;
                case OperandType.InlineSwitch:
                    at += 4 + (4 * BitConverter.ToInt32(il, at));
                    break;
                default:   // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
                    at += 4;
                    break;
            }
        }
    }
}
