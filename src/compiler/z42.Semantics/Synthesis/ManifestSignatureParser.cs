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
    /// used to expand <c>Self</c>.
    /// </summary>
    public static TypeExpr ParseReturn(string sig, string selfTypeName, Span span)
    {
        var s = sig.Trim();

        if (s == "void")    return new VoidType(span);
        if (s == "Self")    return new NamedType(selfTypeName, span);

        if (s_primitives.Contains(s))
            return new NamedType(s, span);

        throw Unsupported(sig, span, position: "return");
    }

    /// <summary>
    /// Parse a manifest param <c>type</c> string. Returns
    /// <c>(isReceiver, type)</c>: when <c>isReceiver==true</c> the parameter
    /// is the implicit <c>this</c> receiver and must be dropped from the
    /// synthesized z42 method signature; <paramref name="type"/> is then null.
    /// Only valid for <paramref name="firstParam"/>=true.
    /// </summary>
    public static (bool IsReceiver, TypeExpr? Type) ParseParam(
        string sig, string selfTypeName, bool firstParam, Span span)
    {
        var s = sig.Trim();

        if (firstParam && (s == "*mut Self" || s == "*const Self"))
            return (IsReceiver: true, Type: null);

        if (s == "Self")
            return (IsReceiver: false, Type: new NamedType(selfTypeName, span));

        if (s_primitives.Contains(s))
            return (IsReceiver: false, Type: new NamedType(s, span));

        throw Unsupported(sig, span, position: "parameter");
    }

    private static NativeImportException Unsupported(string sig, Span span, string position)
        => new(
            DiagnosticCodes.NativeImportSynthesisFailure,
            $"manifest {position} type `{sig}` is not supported by C11b synthesizer " +
            "(whitelist: primitives, `Self`, `*mut/const Self`)",
            span);
}
