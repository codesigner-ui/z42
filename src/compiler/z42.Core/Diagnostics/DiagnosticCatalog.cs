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

        [DiagnosticCodes.InvalidImpl] = new(
            "Invalid extern impl block",
            "An `impl Trait for Type { ... }` block violates extern impl rules: target must be a " +
            "class/struct (local, imported, or primitive struct), trait must be an interface, " +
            "methods must match the interface signature, and no duplicate or missing methods.",
            "impl IGreet for int { string Hello() { return \"\"; } }  // ok (primitive struct)\n" +
            "impl IGreet for SomeInterface { ... }  // error: target is an interface"),

        [DiagnosticCodes.EventFieldExternalAccess] = new(
            "Event field accessed outside its declaring class",
            "Event fields (declared with the `event` keyword) enforce strict access control: " +
            "external code may only `+=` / `-=` to subscribe / unsubscribe via the synthesized " +
            "`add_X` / `remove_X` accessors. Direct read, assignment, or invocation from outside " +
            "the declaring class is rejected so the event source retains exclusive control over " +
            "raising the event. (D-7-residual, 2026-05-04)",
            "class Btn { public event Action<int> OnClick; void Raise() { var f = this.OnClick; } }\n" +
            "void Use(Btn b) { b.OnClick.Invoke(1); }  // E0414: event field cannot be accessed outside `Btn`\n" +
            "void Sub(Btn b, Action<int> h) { b.OnClick += h; }  // ok (desugars to b.add_OnClick(h))"),

        [DiagnosticCodes.InvalidCatchType] = new(
            "Catch type must be a known Exception-derived class",
            "A `catch (T e)` clause requires `T` to be either a known class deriving from " +
            "`Std.Exception` or `Exception` itself. Untyped `catch { }` and `catch (e)` " +
            "remain wildcard catches that match any exception. Catch types are filtered at " +
            "runtime by walking the thrown value's class chain, so `catch (Base e)` matches " +
            "any subclass instance. (catch-by-generic-type, 2026-05-06)",
            "catch (NoSuchType e) { }     // E0420: catch type 'NoSuchType' not found\n" +
            "class Foo {} catch (Foo e) {} // E0420: catch type 'Foo' must derive from Exception\n" +
            "catch (IOException e) { }    // ok"),

        [DiagnosticCodes.InvalidDefaultType] = new(
            "Invalid type in `default(T)` expression",
            "The type argument to `default(T)` must be a fully-resolved type known at " +
            "compile time. Unknown type names trigger E0421 in addition to the normal " +
            "type-not-found diagnostic. Generic type-parameters (e.g. `default(R)` inside " +
            "a `class Foo<R>`) are deferred to Phase 2 (`add-default-generic-typeparam`); " +
            "see `docs/deferred.md` D-8b-3. Result for primitives is the type's zero value " +
            "(0 / 0.0 / false / '\\0'); for reference types (string / class / interface / " +
            "array / nullable) it is `null`. (add-default-expression, 2026-05-06)",
            "default(NoSuchType)                  // E0421: type 'NoSuchType' not found\n" +
            "class Foo<R> { R make() { return default(R); } }  // E0421: generic type-param deferred\n" +
            "default(int)                         // ok, 0\n" +
            "default(string)                      // ok, null"),

        // ── Z05xx: IR code generator ──────────────────────────────────────────

        [DiagnosticCodes.UnsupportedSyntax] = new(
            "Unsupported syntax in code generation",
            "The IR code generator encountered a language construct that is not yet implemented. " +
            "This is a compiler limitation rather than a user error; " +
            "the construct may become supported in a future compiler version.",
            null),

        // ── E06xx: Package / import resolution ────────────────────────────────

        [DiagnosticCodes.NamespaceCollision] = new(
            "Type name collision across packages",
            "Two activated packages declare the same type name in the same namespace. " +
            "Rename one of them, or restrict your `using` declarations so only one package is activated. " +
            "(strict-using-resolution, 2026-04-28)",
            "// packageA declares Foo.Util\n" +
            "// packageB declares Foo.Util\n" +
            "using Foo;  // E0601: `Foo.Util` provided by both `packageA` and `packageB`"),

        [DiagnosticCodes.UnresolvedUsing] = new(
            "Unresolved `using` namespace",
            "A `using` declaration references a namespace that no loaded package provides. " +
            "Check the spelling, or add the providing package as a dependency in z42.toml.",
            "using NoSuch.Pkg;  // E0602: no loaded package provides `NoSuch.Pkg`"),

        [DiagnosticCodes.ReservedNamespace] = new(
            "Package declares reserved namespace",
            "A non-prelude package declares a namespace under the reserved `Std` / `Std.*` prefix. " +
            "These prefixes are reserved for the standard library (z42.core, z42.io, ...). " +
            "Rename your namespace to avoid future conflicts. This is a warning, not an error.",
            "// in some-third-party.zpkg:\n" +
            "namespace Std.Acme;  // W0603: `Std` is reserved"),

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

        [DiagnosticCodes.NativeAttributeMalformed] = new(
            "Malformed [Native] attribute",
            "The Tier 1 form `[Native(lib=\"...\", type=\"...\", entry=\"...\")]` requires all three string-valued keys (lib, type, entry). The legacy `[Native(\"__name\")]` form takes a single string argument. Mixing the forms or using unknown keys is rejected.",
            "[Native(lib=\"numz42\", entry=\"inc\")]   // missing `type=`"),

        // ── E0908: `pinned` block constraints (spec C5) ──────────────────

        [DiagnosticCodes.PinnedNotPinnable] = new(
            "Pinned source must be pinnable",
            "The expression on the right-hand side of `pinned <name> = <expr>` must be a `string` (current C5 limitation; `byte[]` arrives in a follow-up spec).",
            "pinned p = 42 { }   // 42 is int, not pinnable"),

        [DiagnosticCodes.PinnedControlFlow] = new(
            "Early control flow inside `pinned` body",
            "The body of a `pinned` block must use straight-line control flow so the compiler can pair each `pin` with exactly one `unpin`. `return` / `break` / `continue` / `throw` are forbidden inside the body in spec C5.",
            "pinned p = s { return p.len; }   // not allowed in C5"),

        // ── E0909: Native manifest reader (spec C11a) ────────────────────────

        [DiagnosticCodes.ManifestParseError] = new(
            "Native manifest parse failure",
            "A `.z42abi` manifest could not be loaded: the file is missing, the JSON is malformed, the `abi_version` does not match the compiler's expected ABI, or a required field (module / library_name / types) is missing. Re-generate the manifest from the producing native crate, or pin the compiler version that emitted it.",
            "import Counter from \"numz42\";  // numz42.z42abi missing/broken → E0909"),

        // ── E0916: Native import synthesis (spec C11b) ───────────────────────

        [DiagnosticCodes.NativeImportSynthesisFailure] = new(
            "Native import synthesis failure",
            "An `import T from \"lib\";` statement could not be turned into a script-visible class. Common causes: (a) the type name is not present in the manifest's `types[]` array; (b) a method's `params` / `ret` signature uses a type the C11b synthesizer does not yet support (only primitives, `Self`, and `*mut Self` / `*const Self` are accepted; `*const c_char`, user types, arrays, etc. are not yet handled); (c) two `import` statements name the same type but resolve to different libraries; (d) a `kind=\"method\"` entry's first parameter is not a `Self` pointer.",
            "import Foo from \"numz42\";  // numz42.z42abi has no `Foo` type → E0916"),

        // ── E0911 / E0914 / E0915: Test attribute validation (spec R4.A) ─────

        [DiagnosticCodes.TestSignatureInvalid] = new(
            "Test attribute signature invalid",
            "A function decorated with `[Test]` (or `[Benchmark]`) does not satisfy the runner's calling contract. " +
            "[Test] functions must be `fn() -> void` (no parameters, void return, no generic type parameters). " +
            "Two attributes that imply incompatible roles cannot coexist on the same function: `[Test]` is mutually exclusive with `[Benchmark]`.",
            "[Test] void Foo(int x) { }  // [Test] cannot take parameters → E0911"),

        [DiagnosticCodes.BenchmarkSignatureInvalid] = new(
            "Benchmark attribute signature invalid",
            "A function decorated with `[Benchmark]` does not satisfy the bench runner's calling contract. " +
            "Phase R4.A only catches obviously-wrong shapes (non-void return, generic type params); the full " +
            "first-parameter-is-Bencher check lands when the Bencher type ships in R2.C (closure-dependent). " +
            "Until then, runtime errors will surface for malformed [Benchmark] bodies.",
            "[Benchmark] int Bench() { return 0; }  // void return required → E0912"),

        [DiagnosticCodes.ShouldThrowTypeInvalid] = new(
            "ShouldThrow<E> attribute invalid (placeholder)",
            "Reserved for spec R4.B once the parser supports generic attribute syntax `[ShouldThrow<E>]`. " +
            "Will fire when E is undefined, not a subtype of `Std.Exception`, or applied without `[Test]`. " +
            "Currently the parser does not accept `[ShouldThrow<...>]` so this code is unreachable.",
            null),

        [DiagnosticCodes.SkipReasonMissing] = new(
            "Skip attribute missing reason or used standalone",
            "`[Skip(...)]` requires a non-empty `reason:` named argument explaining why the test is skipped. " +
            "Additionally, `[Skip]` and `[Ignore]` only make sense on a function that is also `[Test]` or `[Benchmark]` — " +
            "decorating a regular non-test function with these attributes is treated as a programmer mistake.",
            "[Skip] void Foo() { }  // missing [Test] + missing reason → E0914"),

        [DiagnosticCodes.SetupTeardownSignatureInvalid] = new(
            "Setup/Teardown attribute signature invalid",
            "Functions decorated with `[Setup]` or `[Teardown]` must be `fn() -> void` (no parameters, void return). " +
            "These attributes are mutually exclusive with `[Test]`, `[Benchmark]`, `[Skip]`, and `[Ignore]` — " +
            "setup/teardown hooks are infrastructure, not tests themselves.",
            "[Setup] void Init(int seed) { }  // setup cannot take parameters → E0915"),
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
