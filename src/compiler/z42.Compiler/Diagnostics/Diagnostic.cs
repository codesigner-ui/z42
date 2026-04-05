using Z42.Compiler.Lexer;

namespace Z42.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

/// <summary>
/// A single compiler diagnostic (error, warning, or info).
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string             Code,      // e.g. "Z0001"
    string             Message,
    Span               Span
)
{
    public bool IsError   => Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Severity == DiagnosticSeverity.Warning;

    public override string ToString() =>
        $"{Span.File}({Span.Line},{Span.Column}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";

    // ── Factory helpers ────────────────────────────────────────────────────

    public static Diagnostic Error(string code, string message, Span span) =>
        new(DiagnosticSeverity.Error, code, message, span);

    public static Diagnostic Warning(string code, string message, Span span) =>
        new(DiagnosticSeverity.Warning, code, message, span);

    public static Diagnostic Info(string code, string message, Span span) =>
        new(DiagnosticSeverity.Info, code, message, span);
}

/// <summary>
/// Well-known diagnostic codes.
/// Z01xx — Lexer
/// Z02xx — Parser / syntax
/// Z03xx — Features (gated syntax used when disabled)
/// Z04xx — Type checker
/// Z05xx — IrGen / code generator
/// Z09xx — Native / InternalCall
/// </summary>
public static class DiagnosticCodes
{
    // Lexer
    public const string UnterminatedString   = "Z0101";
    public const string InvalidEscape        = "Z0102";
    public const string InvalidNumericLit    = "Z0103";

    // Parser
    public const string UnexpectedToken      = "Z0201";
    public const string ExpectedToken        = "Z0202";
    public const string UnexpectedEof        = "Z0203";
    public const string MissingReturnType    = "Z0204";
    public const string AmbiguousExpression  = "Z0205";

    // Feature gates
    public const string FeatureDisabled      = "Z0301";

    // Type checker
    public const string UndefinedSymbol      = "Z0401";
    public const string TypeMismatch         = "Z0402";
    public const string MissingReturn        = "Z0403";
    public const string AccessViolation      = "Z0404";  // private member accessed from outside class
    public const string InvalidModifier      = "Z0405";  // illegal modifier (e.g. combined, or on enum member)
    public const string IntLiteralOutOfRange = "Z0406";  // integer literal exceeds target type's range

    // IrGen
    public const string UnsupportedSyntax    = "Z0501";

    // Native / InternalCall
    public const string UnknownIntrinsic         = "Z0901"; // [Native("__unknown")] not in NativeTable
    public const string IntrinsicParamCountMismatch = "Z0902"; // param count doesn't match NativeTable
    public const string ExternRequiresNative     = "Z0903"; // extern method missing [Native] attribute
    public const string NativeRequiresExtern     = "Z0904"; // [Native] attribute on non-extern method
}
