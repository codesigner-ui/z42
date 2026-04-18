using System.Text;

namespace Z42.Core.Diagnostics;

/// <summary>
/// Extended documentation for every diagnostic code.
/// Used by <c>z42c --explain &lt;code&gt;</c> and future IDE tooling.
///
/// Each entry has:
///   Title       — one-line summary shown in error output
///   Description — what causes the error and why
///   Example     — minimal z42 snippet that triggers the error (null if N/A)
/// </summary>
public sealed record DiagnosticEntry(
    string  Title,
    string  Description,
    string? Example = null);

public static class DiagnosticCatalog
{
    public static readonly IReadOnlyDictionary<string, DiagnosticEntry> All =
        new Dictionary<string, DiagnosticEntry>
    {
        // ── Z01xx: Lexer ──────────────────────────────────────────────────────

        [DiagnosticCodes.UnterminatedString] = new(
            "Unterminated string literal",
            "A string or char literal was opened but never closed before the end of the line or file.",
            """string s = "hello;  // missing closing quote"""),

        [DiagnosticCodes.InvalidEscape] = new(
            "Invalid escape sequence",
            "The escape sequence in a string or char literal is not one of the recognised sequences " +
            @"(\n \r \t \\ \"" \' \0 \uXXXX).",
            """string s = "\q";  // \q is not a valid escape"""),

        [DiagnosticCodes.InvalidNumericLit] = new(
            "Invalid numeric literal",
            "The numeric literal is syntactically malformed (e.g. a hex prefix `0x` with no digits, " +
            "or a floating-point exponent with no digits).",
            "int x = 0x;  // hex prefix with no digits"),

        // ── Z02xx: Parser ─────────────────────────────────────────────────────

        [DiagnosticCodes.UnexpectedToken] = new(
            "Unexpected token",
            "The parser encountered a token it did not expect at this position. " +
            "Check for typos, mismatched brackets, or a missing operator.",
            "int x = * 5;  // leading `*` is not valid here"),

        [DiagnosticCodes.ExpectedToken] = new(
            "Expected token",
            "The parser expected a specific token (e.g. `;`, `)`, `{`) but found something else.",
            "int x = 1   // missing semicolon"),

        [DiagnosticCodes.UnexpectedEof] = new(
            "Unexpected end of file",
            "The source file ended before a construct was complete. " +
            "A common cause is a missing closing `}` for a function or class body.",
            "void Foo() {  // missing closing brace"),

        [DiagnosticCodes.MissingReturnType] = new(
            "Missing return type",
            "A function declaration does not have a return type annotation. " +
            "Every function must declare its return type explicitly (use `void` for procedures).",
            "Foo() { return 42; }  // must be: int Foo() { ... }"),

        [DiagnosticCodes.AmbiguousExpression] = new(
            "Ambiguous expression",
            "The expression cannot be parsed unambiguously. " +
            "Add parentheses to clarify the intended grouping.",
            null),

        // ── Z03xx: Feature gates ──────────────────────────────────────────────

        [DiagnosticCodes.FeatureDisabled] = new(
            "Language feature not enabled",
            "A syntax construct was used that requires an opt-in language feature. " +
            "Enable the feature in LanguageFeatures or use a pre-built profile that includes it.",
            "var f = (x) => x + 1;  // lambda requires LanguageFeatures.Lambda = true"),

        // ── Z04xx: Type checker ───────────────────────────────────────────────

        [DiagnosticCodes.UndefinedSymbol] = new(
            "Undefined symbol",
            "A variable, function, type, or other symbol was referenced before it was declared, " +
            "or it does not exist in the current scope.",
            "int x = y + 1;  // 'y' was never declared"),

        [DiagnosticCodes.TypeMismatch] = new(
            "Type mismatch",
            "A value of an incompatible type was used where a different type is required. " +
            "This covers: assignment type incompatibility, operator type mismatches, " +
            "non-bool conditions, arity mismatches in calls, and return type mismatches.",
            "int x = \"hello\";  // cannot assign string to int"),

        [DiagnosticCodes.MissingReturn] = new(
            "Missing return value",
            "A non-void function has at least one code path that reaches the end without returning a value.",
            "int Abs(int n) { if (n >= 0) return n; }  // missing return for n < 0"),

        [DiagnosticCodes.AccessViolation] = new(
            "Private member access violation",
            "A field or method declared `private` was accessed from outside its declaring class.",
            "class Foo { private int x = 1; }\n" +
            "void Main() { var f = new Foo(); int v = f.x; }  // x is private"),

        [DiagnosticCodes.InvalidModifier] = new(
            "Invalid modifier combination",
            "A modifier was applied in a context where it is not allowed, " +
            "or two mutually exclusive modifiers were combined (e.g. `abstract sealed`, " +
            "`abstract` on a non-overridable member, or modifiers on enum members).",
            "abstract sealed class Foo {}  // cannot be both abstract and sealed"),

        [DiagnosticCodes.IntLiteralOutOfRange] = new(
            "Integer literal out of range",
            "An integer literal value falls outside the valid range of its declared target type. " +
            "Either use a wider type, add an explicit cast, or change the literal.",
            "i8 x = 200;   // i8 range is -128 to 127\n" +
            "u8 y = -1;    // u8 range is 0 to 255"),

        [DiagnosticCodes.UninitializedVariable] = new(
            "Variable used before assignment",
            "A local variable declared without an initializer was read before being assigned a value on all code paths. " +
            "Either assign a value before the read or provide an initializer in the declaration.",
            "int x;\n" +
            "int y = x + 1;  // error: x may be used before being assigned"),

        [DiagnosticCodes.DuplicateDeclaration] = new(
            "Duplicate declaration",
            "A function, class, variable, or parameter with the same name is already declared in the current scope.",
            "int x = 1;\nint x = 2;  // duplicate variable 'x'"),

        [DiagnosticCodes.VoidAssignment] = new(
            "Void assigned to variable",
            "A void-returning expression was used as an initializer for a variable. " +
            "Void values cannot be stored in variables.",
            "void Greet() { }\nvar x = Greet();  // cannot assign void to variable"),

        [DiagnosticCodes.InvalidBreakContinue] = new(
            "Break/continue outside of loop",
            "A `break` or `continue` statement was used outside of a loop body (while, for, foreach, do-while).",
            "void Main() { break; }  // break outside of loop"),

        [DiagnosticCodes.InvalidInheritance] = new(
            "Invalid inheritance",
            "A class or struct violates inheritance rules: inheriting from a sealed class, " +
            "struct with a base class, override without matching virtual/abstract, or missing abstract implementations.",
            "sealed class Base {}\nclass Derived : Base {}  // cannot inherit from sealed class"),

        [DiagnosticCodes.InterfaceMismatch] = new(
            "Interface implementation mismatch",
            "A class declares that it implements an interface but is missing one or more required methods, " +
            "or a method has a different signature than the interface declaration.",
            "interface IFoo { void Bar(); }\nclass Foo : IFoo {}  // Foo does not implement Bar()"),

        // ── Z05xx: IR code generator ──────────────────────────────────────────

        [DiagnosticCodes.UnsupportedSyntax] = new(
            "Unsupported syntax in code generation",
            "The IR code generator encountered a language construct that is not yet implemented. " +
            "This is a compiler limitation rather than a user error; " +
            "the construct may become supported in a future compiler version.",
            null),

        // ── Z09xx: Internal compiler error ──────────────────────────────────

        [DiagnosticCodes.InternalCompilerError] = new(
            "Internal compiler error (ICE)",
            "An unexpected error occurred inside the compiler. " +
            "This is a compiler bug, not a user error. " +
            "Please report this issue with the full error message and source file.",
            null),

        // ── Z09xx: Native / InternalCall ─────────────────────────────────────

        [DiagnosticCodes.ExternRequiresNative] = new(
            "Extern method requires [Native] attribute",
            "A method declared `extern` must also have a `[Native]` attribute specifying the runtime intrinsic name.",
            "extern int ReadByte();  // missing [Native(\"...\")]"),

        [DiagnosticCodes.NativeRequiresExtern] = new(
            "Native attribute on non-extern method",
            "The `[Native]` attribute can only be applied to methods declared with the `extern` keyword.",
            "[Native(\"file_read\")] int ReadByte() { return 0; }  // not extern"),
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// Returns the catalog entry for <paramref name="code"/>, or null if not registered.
    public static DiagnosticEntry? TryGet(string code) =>
        All.TryGetValue(code, out var e) ? e : null;

    /// Formats a detailed human-readable explanation of <paramref name="code"/>.
    /// Suitable for printing to a terminal (mirrors `rustc --explain`).
    public static string Explain(string code)
    {
        if (!All.TryGetValue(code, out var e))
            return $"No documentation found for error code {code}.\n" +
                   $"Run `z42c --list-errors` to see all known codes.";

        var sb = new StringBuilder();
        sb.AppendLine($"error[{code}]: {e.Title}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();
        sb.AppendLine(e.Description);
        if (e.Example != null)
        {
            sb.AppendLine();
            sb.AppendLine("Example:");
            foreach (var line in e.Example.Split('\n'))
                sb.AppendLine($"  {line}");
        }
        return sb.ToString().TrimEnd();
    }

    /// Prints a compact table of all known codes (for `--list-errors`).
    public static string ListAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("z42 diagnostic codes:");
        sb.AppendLine();

        string? currentPrefix = null;
        foreach (var (code, entry) in All.OrderBy(kv => kv.Key))
        {
            string prefix = code[..4]; // "E010", "E020", ...
            if (prefix != currentPrefix)
            {
                if (currentPrefix != null) sb.AppendLine();
                currentPrefix = prefix;
            }
            sb.AppendLine($"  {code}  {entry.Title}");
        }

        sb.AppendLine();
        sb.AppendLine("Use `z42c --explain <code>` for full details.");
        return sb.ToString().TrimEnd();
    }
}
