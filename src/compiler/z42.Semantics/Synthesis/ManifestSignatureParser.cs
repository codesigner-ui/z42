using Z42.Core.Diagnostics;
using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Synthesis;

/// <summary>
/// Translates manifest type signature strings (e.g. <c>"i64"</c>,
/// <c>"*mut Self"</c>, <c>"Self"</c>) into z42 <see cref="TypeExpr"/> nodes.
///
/// Whitelist (C11b — Path B1):
///   • <c>void</c>
///   • <c>i8</c> / <c>i16</c> / <c>i32</c> / <c>i64</c>
///   • <c>u8</c> / <c>u16</c> / <c>u32</c> / <c>u64</c>
///   • <c>f32</c> / <c>f64</c>
///   • <c>bool</c>
///   • <c>Self</c>            — only as return type
///   • <c>*mut Self</c> / <c>*const Self</c> — only as receiver (first param)
///
/// Anything else raises <see cref="NativeImportException"/> with
/// <see cref="DiagnosticCodes.NativeImportSynthesisFailure"/>. The whitelist
/// is intentionally narrow; later specs (C11e+) extend it.
/// </summary>
public static class ManifestSignatureParser
{
    private static readonly HashSet<string> s_primitives = new()
    {
        "void",
        "i8", "i16", "i32", "i64",
        "u8", "u16", "u32", "u64",
        "f32", "f64",
        "bool",
    };

    /// <summary>
    /// Parse a manifest <c>ret</c> string into a z42 <see cref="TypeExpr"/>.
    /// <paramref name="selfTypeName"/> is the enclosing manifest type name —
    /// used to expand <c>Self</c>. <paramref name="knownNativeTypes"/> is the
    /// set of native type names imported by the current compilation unit
    /// (always includes <paramref name="selfTypeName"/>); used by C11e to
    /// resolve <c>*mut/*const &lt;Other&gt;</c> against in-scope imports.
    /// </summary>
    public static TypeExpr ParseReturn(
        string sig, string selfTypeName,
        IReadOnlySet<string> knownNativeTypes,
        Span span)
    {
        var s = sig.Trim();

        if (s == "void")    return new VoidType(span);
        if (s == "Self")    return new NamedType(selfTypeName, span);

        if (s == "*const c_char" || s == "*mut c_char")
            throw new NativeImportException(
                DiagnosticCodes.NativeImportSynthesisFailure,
                $"manifest return type `{sig}` (c_char return) requires an " +
                "ownership protocol (who frees the C string?) and is tracked in " +
                "C11f; for now use a void return + out-param or wrap in a typed handle",
                span);

        if (TryParsePointerToOther(s, knownNativeTypes, selfTypeName, span, out var named))
            return named!;

        if (s_primitives.Contains(s))
            return new NamedType(s, span);

        throw Unsupported(sig, span, position: "return", knownNativeTypes);
    }

    /// <summary>
    /// Parse a manifest param <c>type</c> string. Returns
    /// <c>(isReceiver, type)</c>: when <c>isReceiver==true</c> the parameter
    /// is the implicit <c>this</c> receiver and must be dropped from the
    /// synthesized z42 method signature; <paramref name="type"/> is then null.
    /// Only valid for <paramref name="firstParam"/>=true.
    /// </summary>
    public static (bool IsReceiver, TypeExpr? Type) ParseParam(
        string sig, string selfTypeName,
        IReadOnlySet<string> knownNativeTypes,
        bool firstParam, Span span)
    {
        var s = sig.Trim();

        if (firstParam && (s == "*mut Self" || s == "*const Self"))
            return (IsReceiver: true, Type: null);

        if (s == "*const c_char" || s == "*mut c_char")
            return (IsReceiver: false, Type: new NamedType("string", span));

        if (TryParsePointerToOther(s, knownNativeTypes, selfTypeName, span, out var named))
            return (IsReceiver: false, Type: named!);

        if (s == "Self")
            return (IsReceiver: false, Type: new NamedType(selfTypeName, span));

        if (s_primitives.Contains(s))
            return (IsReceiver: false, Type: new NamedType(s, span));

        throw Unsupported(sig, span, position: "parameter", knownNativeTypes);
    }

    /// <summary>
    /// C11e: recognise <c>*mut &lt;X&gt;</c> / <c>*const &lt;X&gt;</c> where X is
    /// neither <c>Self</c> nor <c>c_char</c>. X must be in
    /// <paramref name="known"/> — otherwise raise an unknown-type error.
    /// Returns false (does not match) when the shape isn't a pointer-to-other.
    /// </summary>
    private static bool TryParsePointerToOther(
        string s, IReadOnlySet<string> known, string selfName,
        Span span, out NamedType? named)
    {
        named = null;
        string? targetName = null;
        if (s.StartsWith("*mut "))   targetName = s["*mut ".Length..].Trim();
        else if (s.StartsWith("*const ")) targetName = s["*const ".Length..].Trim();
        if (targetName is null) return false;
        if (targetName == "Self" || targetName == "c_char") return false;

        if (!known.Contains(targetName))
            throw Unknown(targetName, span);

        named = new NamedType(targetName, span);
        return true;
    }

    private static NativeImportException Unknown(string typeName, Span span) => new(
        DiagnosticCodes.NativeImportSynthesisFailure,
        $"manifest references native type `{typeName}` but no matching " +
        $"`import {typeName} from \"...\";` is in scope",
        span);

    private static NativeImportException Unsupported(
        string sig, Span span, string position, IReadOnlySet<string> known)
    {
        var importedList = known.Count == 0 ? "(none)" : string.Join(", ", known);
        return new(
            DiagnosticCodes.NativeImportSynthesisFailure,
            $"manifest {position} type `{sig}` is not supported by C11e synthesizer " +
            "(whitelist: primitives, `Self`, `*mut/const Self`, `*const c_char` (param-only), " +
            $"`*mut/const <Imported>`; currently-imported native types: {importedList})",
            span);
    }
}
