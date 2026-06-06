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
        // ── E01xx: Lexer ──────────────────────────────────────────────────────

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

        // ── E02xx: Parser ─────────────────────────────────────────────────────

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

        // ── E03xx: Feature gates ──────────────────────────────────────────────

        [DiagnosticCodes.FeatureDisabled] = new(
            "Language feature not enabled",
            "A syntax construct was used that requires an opt-in language feature. " +
            "Enable the feature in LanguageFeatures or use a pre-built profile that includes it.",
            "var f = (x) => x + 1;  // lambda requires LanguageFeatures.Lambda = true"),

        // ── E04xx: Type checker ───────────────────────────────────────────────

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

        [DiagnosticCodes.GenericFuncConstraintViolation] = new(
            "Type argument does not satisfy function-type constraint",
            "A generic parameter has a function/delegate constraint " +
            "(`where T: Func<int, R>` / `where T: Action<...>` / `where T: (int) -> R`) " +
            "and the supplied type argument is not assignable to the constraint signature. " +
            "Assignability follows the usual variance rules: parameter types are " +
            "contravariant (the argument's params can be supertypes), the return type is " +
            "covariant (the argument's return can be a subtype). " +
            "(add-generic-func-constraint, 2026-05-11)",
            "R Apply<T, R>(T fn, int x) where T: Func<int, R> { return fn(x); }\n" +
            "Apply<Func<string,int>, int>(s => int.Parse(s), 5)  // E0422: param 'string' is not contravariantly compatible with 'int'\n" +
            "Apply<Func<int,int>, int>(n => n * 2, 5)            // ok"),

        [DiagnosticCodes.InvalidFuncConstraint] = new(
            "Function-type constraint cannot combine with other constraints (v1)",
            "In the first release of function-type constraints, only standalone forms are " +
            "permitted. Mixing a function constraint with class / interface / `new()` / " +
            "`class` / `struct` / `enum` constraints on the same type parameter is not " +
            "supported. Likewise, only one function constraint per type parameter. " +
            "Combinations such as `where T: Func<int,int> + IFormattable` may be relaxed in " +
            "a future release. (add-generic-func-constraint, 2026-05-11)",
            "void X<T>() where T: Func<int,int>             // ok\n" +
            "void X<T>() where T: Func<int,int> + ICloneable // E0423: cannot combine\n" +
            "void X<T>() where T: Func<int,int>, T: Action  // E0423: multiple func constraints"),

        [DiagnosticCodes.IllegalCast] = new(
            "Illegal type cast",
            "Only the following explicit casts are supported: numeric ↔ numeric, char ↔ numeric, " +
            "object ↔ anything (runtime-checked). Casts between bool and numeric / string and " +
            "numeric / etc. are rejected at compile time. Use a conditional expression for " +
            "bool conversions, and `Parse` / `ToString` for string conversions. " +
            "(fix-numeric-cast-lowering, 2026-05-13)",
            "long n = (long)3.7;           // ok: 3\n" +
            "int  i = (int)'A';            // ok: 65\n" +
            "int  i = (int)true;           // E0424: cannot cast bool ↔ numeric\n" +
            "long n = (long)\"42\";          // E0424: cannot cast string ↔ numeric; use long.Parse"),

        // ── E05xx: IR code generator ──────────────────────────────────────────

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
            "Rename your namespace to avoid future conflicts. This is a dependency-scan warning " +
            "(an already-built third-party zpkg squats `Std.*`); declaring it in your own source " +
            "is a hard error — see E0605.",
            "// in some-third-party.zpkg:\n" +
            "namespace Std.Acme;  // W0603: `Std` is reserved"),

        [DiagnosticCodes.ReservedNamespaceDeclaration] = new(
            "Source declares reserved standard-library namespace",
            "A third-party package (one whose name does not start with `z42.`) declares a " +
            "namespace under the reserved `Std` / `Std.*` prefix in its own source. These prefixes " +
            "belong exclusively to the standard library (z42.core, z42.io, ...), the same way Rust " +
            "reserves `std` / `core` / `alloc`. This guarantees that any `Std.*` a program uses " +
            "resolves to the official, auto-available stdlib and can never be shadowed by a " +
            "third-party package. Rename your namespace to your own prefix. (This is the hard-error " +
            "counterpart of W0603, which only warns when consuming an already-built offending zpkg.)",
            "// in my-app (package name `acme.app`):\n" +
            "namespace Std.Widgets;  // E0605: `Std.*` is reserved for stdlib — use `Acme.Widgets`"),

        [DiagnosticCodes.CapturedValueSnapshotAssign] = new(
            "Assignment to captured value-type variable is local to the closure",
            "A closure body writes to a captured value-type variable (int / bool / float / " +
            "char / struct / enum). Captures of value types are by-snapshot (closure.md §4.1), " +
            "so the write only mutates the closure's local copy — the outer scope's slot keeps " +
            "its original value. This is almost always a silent bug (e.g. a thread lambda " +
            "trying to publish a result back to the calling test). To actually share mutable " +
            "state across the boundary use a class field (closure.md §4.4) or a single-element " +
            "array as a cell. This is a warning, not an error: the language explicitly allows " +
            "by-snapshot capture as a semantic feature.",
            "bool seen = false;\n" +
            "Thread t = Thread.Start(() => { seen = true; });   // W0604: lost write\n" +
            "t.Join();\n" +
            "// `seen` is still false here.\n" +
            "\n" +
            "// Fix — cell pattern:\n" +
            "bool[] seen = new bool[1];\n" +
            "Thread t = Thread.Start(() => { seen[0] = true; }); // ok\n" +
            "t.Join();"),

        // ── E09xx: Internal compiler error ──────────────────────────────────

        [DiagnosticCodes.InternalCompilerError] = new(
            "Internal compiler error (ICE)",
            "An unexpected error occurred inside the compiler. " +
            "This is a compiler bug, not a user error. " +
            "Please report this issue with the full error message and source file.",
            null),

        // ── E09xx: Native / InternalCall ─────────────────────────────────────

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
            "A function decorated with `[Benchmark]` must be `fn() -> void` with no parameters and no generic " +
            "type parameters — same shape as `[Test]`. The runner dispatches benchmarks via the same in-process " +
            "execution path; the test author constructs a `Std.Test.Bencher` inside the body to measure work. " +
            "add-benchmark-runner-dispatch (2026-05-31) flipped the contract from `void f(Bencher b)` after " +
            "discovering the runner-side Bencher construction would need infrastructure not yet built; the " +
            "Bencher-arg form may return in a future spec via compiler-generated trampolines.",
            "[Benchmark] int Bench() { return 0; }            // void return required → E0912\n" +
            "[Benchmark] void Bench(Bencher b) { … }          // zero params required → E0912 (post-2026-05-31)\n" +
            "[Benchmark] void Bench() {\n" +
            "    var b = new Bencher();\n" +
            "    b.iter(() => doWork());\n" +
            "    b.printSummary(\"work\");\n" +
            "}                                                 // OK"),

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

        // ── E10xx: Call-site argument binding (spec add-named-arguments) ─────

        [DiagnosticCodes.PositionalAfterNamed] = new(
            "Positional argument after named argument",
            "Once a named argument (`name: value`) is supplied at a call site, all subsequent arguments must also be named. " +
            "Reorder so positional arguments come first, or convert the offending argument to a named one.",
            "Draw(color: \"red\", 2);  // positional `2` after `color:` is invalid"),

        [DiagnosticCodes.UnknownArgumentName] = new(
            "Unknown argument name",
            "The name in a `name: value` argument does not match any parameter of the callee (or of any compatible overload).",
            "void Draw(string color) { }\nDraw(colour: \"red\");  // typo — should be `color`"),

        [DiagnosticCodes.DuplicateArgumentName] = new(
            "Duplicate named argument",
            "The same parameter name is supplied twice in a single call. Each parameter can be set at most once via a named argument.",
            "Draw(color: \"red\", color: \"blue\");  // `color` provided twice"),

        [DiagnosticCodes.ParameterDoublySpecified] = new(
            "Parameter specified by both positional and named argument",
            "A parameter received a value both from a positional argument and from a later named argument. Use one form or the other.",
            "void Draw(string color, int width = 1) { }\nDraw(\"red\", color: \"blue\");  // `color` set positionally and named"),

        [DiagnosticCodes.MissingRequiredArgument] = new(
            "Missing required argument",
            "After binding all positional and named arguments, a required parameter (one without a default value) is still unset.",
            "void Draw(string color, int width) { }\nDraw(width: 2);  // `color` not supplied"),

        [DiagnosticCodes.SetupTeardownSignatureInvalid] = new(
            "Setup/Teardown attribute signature invalid",
            "Functions decorated with `[Setup]` or `[Teardown]` must be `fn() -> void` (no parameters, void return). " +
            "These attributes are mutually exclusive with `[Test]`, `[Benchmark]`, `[Skip]`, and `[Ignore]` — " +
            "setup/teardown hooks are infrastructure, not tests themselves.",
            "[Setup] void Init(int seed) { }  // setup cannot take parameters → E0915"),

        [DiagnosticCodes.TimeoutValueInvalid] = new(
            "Timeout attribute argument invalid",
            "The `[Timeout(milliseconds: <int>)]` attribute requires a single named-arg `milliseconds:` whose value " +
            "is an integer literal in the range (0, int.MaxValue]. The attribute must be paired with `[Test]` or " +
            "`[Benchmark]` on the same method, and cannot appear more than once.",
            "[Test] [Timeout(milliseconds: 5000)] void slow_io() { ... }       // OK\n" +
            "[Test] [Timeout(milliseconds: 0)]    void bad_zero() { }          // → E0917 (must be > 0)\n" +
            "       [Timeout(milliseconds: 1000)] void lonely() { }            // → E0917 (no [Test])\n" +
            "[Test] [Timeout(milliseconds: \"5000\")] void typo() { }           // → E0917 (must be integer literal)"),
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// External catalogs (e.g. <c>WorkspaceCatalog</c>) register here so the
    /// explain / list commands can route across code-spaces (E### / W### / Z###).
    /// Z42.Project bootstraps its registration in a module initializer.
    private static readonly List<Func<string, DiagnosticEntry?>> _externalLookups = new();
    private static readonly List<Func<IEnumerable<KeyValuePair<string, DiagnosticEntry>>>> _externalEnumerators = new();

    public static void RegisterExternal(
        Func<string, DiagnosticEntry?> lookup,
        Func<IEnumerable<KeyValuePair<string, DiagnosticEntry>>> enumerator)
    {
        _externalLookups.Add(lookup);
        _externalEnumerators.Add(enumerator);
    }

    /// Returns the catalog entry for <paramref name="code"/>, or null if not registered.
    /// Searches the built-in E### / W#### / Z### catalogs and any externally-registered.
    public static DiagnosticEntry? TryGet(string code)
    {
        if (All.TryGetValue(code, out var e)) return e;
        foreach (var lookup in _externalLookups)
            if (lookup(code) is { } external) return external;
        return null;
    }

    /// Formats a detailed human-readable explanation of <paramref name="code"/>.
    /// Suitable for printing to a terminal (mirrors `rustc --explain`).
    public static string Explain(string code)
    {
        var entry = TryGet(code);
        if (entry is null)
        {
            // 2026-05-11 retire-z-codes: Z#### runtime codes were retired in
            // favour of typed z42 exceptions. Catch by class
            // (Std.InvalidMarshalException, etc.) and read `Message` /
            // `StackTrace` directly.
            if (code.Length >= 1 && (code[0] == 'Z' || code[0] == 'z'))
            {
                return $"Error code {code.ToUpperInvariant()} has been retired.\n" +
                       $"Runtime errors are now typed z42 exceptions — catch the class " +
                       $"(e.g. Std.InvalidMarshalException) and read `Message` / `StackTrace`.\n" +
                       $"Compile-time codes are still under `z42c explain E####`.";
            }
            return $"No documentation found for error code {code}.\n" +
                   $"Run `z42c errors` to see all known codes.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"error[{code}]: {entry.Title}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();
        sb.AppendLine(entry.Description);
        if (entry.Example != null)
        {
            sb.AppendLine();
            sb.AppendLine("Example:");
            foreach (var line in entry.Example.Split('\n'))
                sb.AppendLine($"  {line}");
        }
        return sb.ToString().TrimEnd();
    }

    /// Prints a compact table of all known codes (for `--list-errors`),
    /// across the built-in E### catalog and all externally-registered ones.
    public static string ListAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("z42 diagnostic codes:");
        sb.AppendLine();

        var combined = new List<KeyValuePair<string, DiagnosticEntry>>(All);
        foreach (var enumerator in _externalEnumerators)
            combined.AddRange(enumerator());

        string? currentGroup = null;
        foreach (var (code, entry) in combined.OrderBy(kv => kv.Key))
        {
            string group = GroupKey(code);
            if (group != currentGroup)
            {
                if (currentGroup != null) sb.AppendLine();
                sb.AppendLine($"# {GroupLabel(group)}");
                currentGroup = group;
            }
            sb.AppendLine($"  {code}  {entry.Title}");
        }

        sb.AppendLine();
        sb.AppendLine("Use `z42c explain <code>` for full details.");
        return sb.ToString().TrimEnd();
    }

    /// Map a code prefix to its group bucket. "WS" must precede "W" so
    /// workspace codes (WS###) don't collapse into the compiler-warning
    /// bucket (W#### within the E-catalog, e.g. W0603 reserved namespace).
    private static string GroupKey(string code)
    {
        if (code.StartsWith("WS", StringComparison.Ordinal)) return "WS";
        return code[..1];
    }

    private static string GroupLabel(string group) => group switch
    {
        "E"  => "Compiler diagnostics (E####)",
        "W"  => "Compiler warnings (W####)",
        "WS" => "Workspace / manifest diagnostics (WS###)",
        // 2026-05-11 retire-z-codes: VM runtime Z#### codes retired; runtime
        // errors are typed z42 exceptions now (catch by class, with stack trace).
        _    => $"{group}#### diagnostics",
    };
}
