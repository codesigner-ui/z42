using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// Phase 3 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09): reproducibility
/// hard requirement — same source + same toolchain → byte-identical zbc.
/// Compiles fixed sources twice and asserts byte equality on the produced
/// zbc artifact.
/// </summary>
public class ReproducibilityTests
{
    private static byte[] CompileToZbc(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        diags.HasErrors.Should().BeFalse("source should type-check cleanly");
        var gen = new IrGen(semanticModel: model);
        var module = gen.Generate(cu);
        return ZbcWriter.Write(module, ZbcFlags.None, exports: null, allocator: gen.Allocator);
    }

    [Fact]
    public void TwoCompiles_TrivialMain_ProduceByteIdenticalZbc()
    {
        const string src = """
            namespace Demo;
            int main() { return 42; }
            """;

        var bytes1 = CompileToZbc(src);
        var bytes2 = CompileToZbc(src);

        bytes1.Should().Equal(bytes2,
            because: "byte-identical zbc is the core reproducibility guarantee");
    }

    [Fact]
    public void TwoCompiles_WithClasses_ProduceByteIdenticalZbc()
    {
        const string src = """
            namespace Demo;
            class Bar {
                public int x;
                public Bar(int v) { this.x = v; }
                public int Get() { return this.x; }
            }
            class Aaa {
                public Aaa() {}
            }
            class Foo : Aaa {
                public Foo() : base() {}
                public Bar MakeBar() { return new Bar(5); }
            }
            int main() {
                var f = new Foo();
                return f.MakeBar().Get();
            }
            """;

        var bytes1 = CompileToZbc(src);
        var bytes2 = CompileToZbc(src);

        bytes1.Should().Equal(bytes2,
            because: "multi-class module also reproduces byte-identically");
    }

    [Fact]
    public void ChangingSource_ProducesDifferentZbc()
    {
        // Sanity: different sources do produce different zbc (smoke test that
        // the harness isn't accidentally caching).
        const string src1 = """
            namespace Demo;
            int main() { return 1; }
            """;
        const string src2 = """
            namespace Demo;
            int main() { return 2; }
            """;

        var bytes1 = CompileToZbc(src1);
        var bytes2 = CompileToZbc(src2);

        bytes1.Should().NotEqual(bytes2);
    }
}
