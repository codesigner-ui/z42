using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C6 (`extend-native-attribute`) — `[Native(lib=, type=, entry=)]`
/// targets Tier 1 dispatch (`CallNativeInstr`) instead of legacy L1
/// stdlib builtins (`BuiltinInstr`).
public sealed class NativeAttributeTier1Tests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static IrModule GenModule(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu, imported: null);
        if (diags.HasErrors)
            throw new InvalidOperationException(
                "TypeCheck errors:\n" + string.Join("\n", diags.All));
        return new IrGen(semanticModel: model).Generate(cu);
    }

    // ── Parser ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_LegacyForm_PopulatesNativeIntrinsic()
    {
        const string src = """
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            """;
        var m = ParseCu(src).Classes.Single().Methods.Single();
        m.NativeIntrinsic.Should().Be("__println");
        m.Tier1Binding.Should().BeNull();
    }

    [Fact]
    public void Parse_Tier1Form_PopulatesTier1Binding()
    {
        const string src = """
            class NumZ42 {
                [Native(lib="numz42", type="Counter", entry="inc")]
                public static extern long CounterInc(long ptr);
            }
            """;
        var m = ParseCu(src).Classes.Single().Methods.Single();
        m.NativeIntrinsic.Should().BeNull();
        m.Tier1Binding.Should().NotBeNull();
        m.Tier1Binding!.Lib.Should().Be("numz42");
        m.Tier1Binding!.TypeName.Should().Be("Counter");
        m.Tier1Binding!.Entry.Should().Be("inc");
    }

    [Fact]
    public void Parse_Tier1Form_ArgumentsOrderInsensitive()
    {
        const string src = """
            class N {
                [Native(entry="m", lib="L", type="T")]
                public static extern long F(long x);
            }
            """;
        var m = ParseCu(src).Classes.Single().Methods.Single();
        m.Tier1Binding!.Lib.Should().Be("L");
        m.Tier1Binding!.TypeName.Should().Be("T");
        m.Tier1Binding!.Entry.Should().Be("m");
    }

    [Fact]
    public void TypeCheck_Tier1Form_MissingLib_E0907()
    {
        // Spec C9 — parser accepts partial Tier1 attributes; the missing
        // `lib` is reported by TypeChecker once class-level defaults (here:
        // none) fail to fill it in.
        const string src = """
            class N {
                [Native(type="T", entry="m")]
                public static extern long F(long x);
            }
            """;
        var tokens = new Lexer(src).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported: null);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.NativeAttributeMalformed
            && d.Message.Contains("lib"));
    }

    [Fact]
    public void Parse_Tier1Form_UnknownKey_E0907()
    {
        const string src = """
            class N {
                [Native(lib="L", type="T", entry="m", lulz="z")]
                public static extern long F(long x);
            }
            """;
        var parser = new Parser(new Lexer(src).Tokenize(), LanguageFeatures.Phase1);
        parser.ParseCompilationUnit();
        parser.Diagnostics.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.NativeAttributeMalformed);
    }

    // ── TypeChecker ────────────────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_Tier1Extern_NoDiagnostics()
    {
        const string src = """
            class N {
                [Native(lib="L", type="T", entry="m")]
                public static extern long F(long x);
            }
            void Main() { }
            """;
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported: null);
        diags.HasErrors.Should().BeFalse();
    }

    // ── IR Codegen ─────────────────────────────────────────────────────────────

    [Fact]
    public void Codegen_Tier1_EmitsCallNativeInstr()
    {
        const string src = """
            class N {
                [Native(lib="numz42", type="Counter", entry="inc")]
                public static extern long CounterInc(long ptr);
            }
            void Main() { }
            """;
        var m = GenModule(src);
        var stubFn = m.Functions.Single(f => f.Name.EndsWith(".CounterInc"));
        var instrs = stubFn.Blocks.SelectMany(b => b.Instructions).ToList();

        // Must contain CallNativeInstr targeting numz42::Counter::inc
        var callNative = instrs.OfType<CallNativeInstr>().Should().ContainSingle().Subject;
        callNative.Module.Should().Be("numz42");
        callNative.TypeName.Should().Be("Counter");
        callNative.Symbol.Should().Be("inc");

        // Must NOT contain BuiltinInstr (would mean legacy L1 dispatch)
        instrs.OfType<BuiltinInstr>().Should().BeEmpty();
    }

    // ── Spec C9: class-level [Native] defaults ──────────────────────────────

    [Fact]
    public void Parse_ClassLevelNative_StashedOnClassDecl()
    {
        const string src = """
            [Native(lib="numz42", type="Counter")]
            class N {
                [Native(entry="inc")]
                public static extern long Inc(long ptr);
            }
            """;
        var cls = ParseCu(src).Classes.Single();
        cls.ClassNativeDefaults.Should().NotBeNull();
        cls.ClassNativeDefaults!.Lib.Should().Be("numz42");
        cls.ClassNativeDefaults!.TypeName.Should().Be("Counter");
        cls.ClassNativeDefaults!.Entry.Should().BeNull();

        var m = cls.Methods.Single();
        m.Tier1Binding.Should().NotBeNull();
        m.Tier1Binding!.Lib.Should().BeNull();      // partial — only entry filled
        m.Tier1Binding!.TypeName.Should().BeNull();
        m.Tier1Binding!.Entry.Should().Be("inc");
    }

    [Fact]
    public void Codegen_ClassDefaultsStitched_EmitsFullCallNativeInstr()
    {
        const string src = """
            [Native(lib="numz42", type="Counter")]
            class NumZ42 {
                [Native(entry="inc")]
                public static extern long Inc(long ptr);
            }
            void Main() { }
            """;
        var m = GenModule(src);
        var stubFn = m.Functions.Single(f => f.Name.EndsWith(".Inc"));
        var call = stubFn.Blocks.SelectMany(b => b.Instructions)
                          .OfType<CallNativeInstr>()
                          .Should().ContainSingle().Subject;
        call.Module.Should().Be("numz42");
        call.TypeName.Should().Be("Counter");
        call.Symbol.Should().Be("inc");
    }

    [Fact]
    public void Codegen_MethodOverridesClassLib()
    {
        const string src = """
            [Native(lib="defaultLib", type="DefaultType")]
            class N {
                [Native(lib="overrideLib", entry="m")]
                public static extern long F();
            }
            void Main() { }
            """;
        var m = GenModule(src);
        var stubFn = m.Functions.Single(f => f.Name.EndsWith(".F"));
        var call = stubFn.Blocks.SelectMany(b => b.Instructions)
                          .OfType<CallNativeInstr>()
                          .Should().ContainSingle().Subject;
        call.Module.Should().Be("overrideLib");      // method-level override
        call.TypeName.Should().Be("DefaultType");    // class default
        call.Symbol.Should().Be("m");
    }

    [Fact]
    public void TypeCheck_PartialMethodNoClassDefaults_E0907()
    {
        const string src = """
            class N {
                [Native(entry="m")]
                public static extern long F();
            }
            """;
        var tokens = new Lexer(src).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported: null);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.NativeAttributeMalformed);
    }

    [Fact]
    public void TypeCheck_ClassDefaultsButMethodMissingEntry_E0907()
    {
        const string src = """
            [Native(lib="L", type="T")]
            class N {
                [Native(lib="L", type="T")]
                public static extern long F();
            }
            """;
        var tokens = new Lexer(src).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported: null);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.NativeAttributeMalformed
            && d.Message.Contains("entry"));
    }

    [Fact]
    public void Codegen_FullMethodForm_NoClassDefaults_NoRegression()
    {
        // Verifies that the C6 path (method gives all 3 keys, no class
        // defaults) still works after C9's stitching path was added.
        const string src = """
            class N {
                [Native(lib="L", type="T", entry="m")]
                public static extern long F();
            }
            void Main() { }
            """;
        var m = GenModule(src);
        var stubFn = m.Functions.Single(f => f.Name.EndsWith(".F"));
        var call = stubFn.Blocks.SelectMany(b => b.Instructions)
                          .OfType<CallNativeInstr>()
                          .Should().ContainSingle().Subject;
        call.Module.Should().Be("L");
        call.TypeName.Should().Be("T");
        call.Symbol.Should().Be("m");
    }

    [Fact]
    public void Codegen_Legacy_EmitsBuiltinInstr_NoRegression()
    {
        const string src = """
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            void Main() { }
            """;
        var m = GenModule(src);
        var stubFn = m.Functions.Single(f => f.Name.EndsWith(".WriteLine"));
        var instrs = stubFn.Blocks.SelectMany(b => b.Instructions).ToList();

        instrs.OfType<BuiltinInstr>().Should().ContainSingle()
              .Which.Name.Should().Be("__println");
        instrs.OfType<CallNativeInstr>().Should().BeEmpty();
    }
}
