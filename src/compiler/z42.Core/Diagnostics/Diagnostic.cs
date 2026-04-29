using Z42.Core.Text;

namespace Z42.Core.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

/// <summary>
/// A single compiler diagnostic (error, warning, or info).
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string             Code,      // e.g. "E0001"
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
/// E01xx — Lexer
/// E02xx — Parser / syntax
/// E03xx — Features (gated syntax used when disabled)
/// E04xx — Type checker
/// E05xx — IrGen / code generator
/// E06xx — Package / import resolution（using/namespace 解析、跨包冲突）
/// E09xx — Native / InternalCall
///</summary>
public static class DiagnosticCodes
{
    // Lexer
    public const string UnterminatedString   = "E0101";
    public const string InvalidEscape        = "E0102";
    public const string InvalidNumericLit    = "E0103";

    // Parser
    public const string UnexpectedToken      = "E0201";
    public const string ExpectedToken        = "E0202";
    public const string UnexpectedEof        = "E0203";
    public const string MissingReturnType    = "E0204";
    public const string AmbiguousExpression  = "E0205";

    // Feature gates
    public const string FeatureDisabled      = "E0301";

    // Type checker
    public const string UndefinedSymbol       = "E0401";
    public const string TypeMismatch          = "E0402";  // genuine type incompatibility (assignment, operator, condition, arity)
    public const string MissingReturn         = "E0403";
    public const string AccessViolation       = "E0404";  // private member accessed from outside class
    public const string InvalidModifier       = "E0405";  // illegal modifier (e.g. combined, or on enum member)
    public const string IntLiteralOutOfRange  = "E0406";  // integer literal exceeds target type's range
    public const string UninitializedVariable = "E0407";  // variable used before assignment
    public const string DuplicateDeclaration  = "E0408";  // duplicate function, class, variable, or parameter name
    public const string VoidAssignment        = "E0409";  // cannot assign void to variable
    public const string InvalidBreakContinue  = "E0410";  // break/continue outside of loop
    public const string InvalidInheritance    = "E0411";  // sealed class, struct base, missing abstract impl, override without virtual
    public const string InterfaceMismatch     = "E0412";  // interface method missing or wrong signature
    public const string InvalidImpl           = "E0413";  // extern impl block: bad target/trait, sig mismatch, missing/duplicate method

    // IrGen
    public const string UnsupportedSyntax    = "E0501";

    // Package / import resolution (strict-using-resolution, 2026-04-28)
    public const string NamespaceCollision   = "E0601";  // 两个包同 (ns, class-name) 同时激活
    public const string UnresolvedUsing      = "E0602";  // using 指向不存在的 namespace
    public const string ReservedNamespace    = "W0603";  // 非 prelude 包声明 Std.* 前缀（warn-only）

    // Internal compiler error
    public const string InternalCompilerError = "E0900";

    // Native / InternalCall
    public const string ExternRequiresNative     = "E0903"; // extern method missing [Native] attribute
    public const string NativeRequiresExtern     = "E0904"; // [Native] attribute on non-extern method

    // Native interop — `pinned` block (spec C5)
    public const string PinnedNotPinnable       = "E0908a"; // source of `pinned` is not a pinnable type (string in C5)
    public const string PinnedControlFlow       = "E0908b"; // return/break/continue/throw inside `pinned` body
}
