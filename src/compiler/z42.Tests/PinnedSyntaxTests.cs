using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Project;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C5 (`impl-pinned-syntax`) — `pinned p = s { ... }` block.
///
/// Lexer / Parser / TypeCheck / IR-codegen coverage. End-to-end golden
/// (compile → run) lives under `tests/golden/run/pinned_basic/`.
public sealed class PinnedSyntaxTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IrModule GenModule(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu, imported: null);
        // Throw if there are errors so tests can assert green path
        if (diags.HasErrors)
            throw new InvalidOperationException(
                "TypeCheck errors:\n" + string.Join("\n", diags.All));
        return new IrGen(semanticModel: model).Generate(cu);
    }

    private static List<Diagnostic> CheckDiagnostics(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported: null);
        return diags.All.ToList();
    }

    // ── Lexer ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Lexer_PinnedKeyword_TokenizesAsPinned()
    {
        var tokens = new Lexer("pinned p = s { }").Tokenize();
        tokens[0].Kind.Should().Be(TokenKind.Pinned);
        tokens[1].Kind.Should().Be(TokenKind.Identifier);
        tokens[1].Text.Should().Be("p");
    }

    // ── Parser ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parser_BasicForm_ProducesPinnedStmt()
    {
        const string src = """
            void Main() {
                string s = "hello";
                pinned p = s { }
            }
            """;
        var tokens = new Lexer(src).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var fnBody = cu.Functions.First().Body!.Stmts;
        // Skip the `string s = ...` decl, find the pinned stmt.
        fnBody.OfType<PinnedStmt>().Should().ContainSingle()
            .Which.Name.Should().Be("p");
    }

    // Parser error-recovery behaviour is exercised by the broader parser
    // test-suite; we deliberately avoid pinning specific malformed-input
    // diagnostics here, since the Parser intentionally tries hard to
    // continue after errors. The positive parse path is covered above.

    // ── TypeChecker ────────────────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_StringSource_NoDiagnostics()
    {
        const string src = """
            void Main() {
                string s = "hello";
                pinned p = s {
                    long n = p.len;
                }
            }
            """;
        CheckDiagnostics(src).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_IntSource_Z0908()
    {
        const string src = """
            void Main() {
                int n = 42;
                pinned p = n { }
            }
            """;
        var diags = CheckDiagnostics(src);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.PinnedNotPinnable);
    }

    [Fact]
    public void TypeCheck_ReturnInsidePinned_Z0908()
    {
        const string src = """
            long Main() {
                string s = "hi";
                pinned p = s {
                    return p.len;
                }
                return 0;
            }
            """;
        var diags = CheckDiagnostics(src);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.PinnedControlFlow);
    }

    [Fact]
    public void TypeCheck_BreakInsidePinned_Z0908()
    {
        const string src = """
            void Main() {
                string s = "hi";
                while (true) {
                    pinned p = s {
                        break;
                    }
                }
            }
            """;
        var diags = CheckDiagnostics(src);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.PinnedControlFlow);
    }

    [Fact]
    public void TypeCheck_ThrowInsidePinned_Z0908()
    {
        const string src = """
            using Z42.Core;
            void Main() {
                string s = "hi";
                pinned p = s {
                    throw new Exception("oops");
                }
            }
            """;
        var diags = CheckDiagnostics(src);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.PinnedControlFlow);
    }

    [Fact]
    public void TypeCheck_LenAndPtr_AreLong()
    {
        // No diagnostics — confirms p.len / p.ptr are typed as `long`
        // (otherwise long-typed locals would fail type check).
        const string src = """
            void Main() {
                string s = "hi";
                pinned p = s {
                    long a = p.len;
                    long b = p.ptr;
                }
            }
            """;
        CheckDiagnostics(src).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_UnknownField_Errors()
    {
        const string src = """
            void Main() {
                string s = "hi";
                pinned p = s {
                    long x = p.unknown;
                }
            }
            """;
        var diags = CheckDiagnostics(src);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.UndefinedSymbol);
    }

    // ── IR Codegen ─────────────────────────────────────────────────────────────

    [Fact]
    public void Codegen_EmitsPinPtrUnpinPtrSandwich()
    {
        const string src = """
            void Main() {
                string s = "hi";
                pinned p = s {
                    long n = p.len;
                }
            }
            """;
        var m = GenModule(src);
        var fn = m.Functions[0];
        var instrs = fn.Blocks.SelectMany(b => b.Instructions).ToList();

        // Must contain at least one PinPtrInstr followed by an UnpinPtrInstr.
        var pinIdx = instrs.FindIndex(i => i is PinPtrInstr);
        var unpinIdx = instrs.FindIndex(i => i is UnpinPtrInstr);
        pinIdx.Should().BeGreaterThanOrEqualTo(0, "PinPtr must be emitted");
        unpinIdx.Should().BeGreaterThanOrEqualTo(0, "UnpinPtr must be emitted");
        unpinIdx.Should().BeGreaterThan(pinIdx, "UnpinPtr must come after PinPtr");

        // Body's `p.len` must lower to a FieldGet with field "len".
        instrs.Any(i => i is FieldGetInstr fg && fg.FieldName == "len")
            .Should().BeTrue("p.len should lower to a FieldGet on the pinned view");
    }
}
