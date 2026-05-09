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
/// Phase 3 S3b (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09 redesigned):
/// verifies IrGen produces a populated <see cref="TokenAllocator"/> sibling output.
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
    }

    [Fact]
    public void LocalClass_ResolvesToInsertionIndex()
    {
        const string src = """
            namespace Demo;
            class Foo { public Foo() {} }
            class Bar { public Bar() {} }
            """;

        var (module, allocator) = Compile(src);
        allocator.Should().NotBeNull();
        var pool = new StringPool();

        // module.Classes is in source order: Foo, Bar
        var fooToken = allocator!.ResolveType("Demo.Foo", pool);
        var barToken = allocator.ResolveType("Demo.Bar", pool);

        fooToken.Should().BeLessThan(TokenConsts.ImportBase, "local class");
        barToken.Should().BeLessThan(TokenConsts.ImportBase, "local class");
        fooToken.Should().Be((uint)module.Classes.FindIndex(c => c.Name == "Demo.Foo"));
        barToken.Should().Be((uint)module.Classes.FindIndex(c => c.Name == "Demo.Bar"));
    }

    [Fact]
    public void Local_Function_Is_Registered_As_Intra_Module_Method()
    {
        const string src = """
            namespace Demo;
            int main() { return 0; }
            """;

        var (module, allocator) = Compile(src);
        allocator.Should().NotBeNull();
        var pool = new StringPool();

        var mainToken = allocator!.ResolveMethod("Demo.main", pool);
        mainToken.Should().BeLessThan(TokenConsts.ImportBase, "local fn");
        mainToken.Should().Be((uint)module.Functions.FindIndex(f => f.Name == "Demo.main"));
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
        var pool1 = new StringPool();
        var pool2 = new StringPool();

        a!.ResolveType("Demo.Foo",  pool1).Should().Be(b!.ResolveType("Demo.Foo",  pool2));
        a.ResolveType("Demo.Bar",   pool1).Should().Be(b.ResolveType("Demo.Bar",   pool2));
        a.ResolveMethod("Demo.main",pool1).Should().Be(b.ResolveMethod("Demo.main",pool2));
    }
}
