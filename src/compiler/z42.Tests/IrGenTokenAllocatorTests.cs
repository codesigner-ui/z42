using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// Phase 3 S2 step 2 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09): verifies
/// IrGen produces a populated <see cref="TokenAllocator"/> sibling output.
/// </summary>
public class IrGenTokenAllocatorTests
{
    private static (IrModule Module, TokenAllocator? Allocator) Compile(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        diags.HasErrors.Should().BeFalse("source should type-check cleanly");
        var gen = new IrGen(semanticModel: model);
        var module = gen.Generate(cu);
        return (module, gen.Allocator);
    }

    [Fact]
    public void Allocator_Is_Populated_After_Generate()
    {
        const string src = """
            namespace Demo;
            class Foo {
                public Foo() {}
                public int Add(int a, int b) { return a + b; }
            }
            """;

        var (_, allocator) = Compile(src);

        allocator.Should().NotBeNull(because: "IrGen.Allocator should be set after Generate()");
        allocator!.ImportTable.Should().NotBeNull();
    }

    [Fact]
    public void LocalClass_Is_Registered_As_Intra_Module()
    {
        const string src = """
            namespace Demo;
            class Foo { public Foo() {} }
            class Bar { public Bar() {} }
            """;

        var (_, allocator) = Compile(src);
        allocator.Should().NotBeNull();

        var fooId = allocator!.ResolveType("Demo.Foo");
        var barId = allocator.ResolveType("Demo.Bar");

        fooId.IsImport.Should().BeFalse();
        barId.IsImport.Should().BeFalse();
        // Determinism: TypeIds assigned in Ordinal order — "Demo.Bar" < "Demo.Foo"
        barId.Value.Should().Be(0u);
        fooId.Value.Should().Be(1u);
    }

    [Fact]
    public void Local_Function_Is_Registered_As_Intra_Module_Method()
    {
        const string src = """
            namespace Demo;
            int main() { return 0; }
            """;

        var (_, allocator) = Compile(src);
        allocator.Should().NotBeNull();

        var mainId = allocator!.ResolveMethod("Demo.main");
        mainId.IsImport.Should().BeFalse();
    }

    [Fact]
    public void TwoCompiles_SameSource_ProduceSameTokens()
    {
        const string src = """
            namespace Demo;
            class Foo { public Foo() {} }
            class Bar { public Bar() {} }
            int main() { return 0; }
            """;

        var (_, a) = Compile(src);
        var (_, b) = Compile(src);

        a.Should().NotBeNull();
        b.Should().NotBeNull();

        a!.ResolveType("Demo.Foo").Should().Be(b!.ResolveType("Demo.Foo"));
        a.ResolveType("Demo.Bar").Should().Be(b.ResolveType("Demo.Bar"));
        a.ResolveMethod("Demo.main").Should().Be(b.ResolveMethod("Demo.main"));
    }
}
