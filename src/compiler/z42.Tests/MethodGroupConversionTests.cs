using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 add-method-group-conversion (D1b) regression tests.
/// 验证：
///   - 顶层函数方法组转换 → emit LoadFnCachedInstr（不是 LoadFnInstr）
///   - 同 fn name 多次出现共享同一 slot id
///   - IrModule.FuncRefCacheSlotCount 与实际 slot 数一致
///   - lambda lifted name 仍走未缓存的 LoadFnInstr（区分流不混淆）
public sealed class MethodGroupConversionTests
{
    private static IrModule GenModule(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        diags.HasErrors.Should().BeFalse("source should type-check cleanly");
        return new IrGen(semanticModel: model).Generate(cu);
    }

    private static IrFunction FindFn(IrModule m, string nameSuffix) =>
        m.Functions.First(f => f.Name.EndsWith(nameSuffix));

    private static List<IrInstr> All(IrFunction fn) =>
        fn.Blocks.SelectMany(b => b.Instructions).ToList();

    [Fact]
    public void Method_Group_Emits_LoadFnCached()
    {
        var m = GenModule("""
            namespace Demo;
            public delegate int IntFn();
            int Helper() { return 42; }
            void Main() {
                IntFn f = Helper;
                var r = f();
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        instrs.OfType<LoadFnCachedInstr>().Any(i => i.Func == "Demo.Helper")
            .Should().BeTrue("var f = Helper should emit LoadFnCached");
        instrs.OfType<LoadFnInstr>().Any(i => i.Func == "Demo.Helper")
            .Should().BeFalse("LoadFn should NOT also be emitted (cached path replaces it)");
    }

    [Fact]
    public void Same_Method_Multiple_Sites_Share_Slot()
    {
        var m = GenModule("""
            namespace Demo;
            public delegate int IntFn();
            int Helper() { return 42; }
            void Main() {
                IntFn a = Helper;
                IntFn b = Helper;
                var x = a();
                var y = b();
            }
            """);
        var slots = All(FindFn(m, ".Main"))
            .OfType<LoadFnCachedInstr>()
            .Where(i => i.Func == "Demo.Helper")
            .Select(i => i.SlotId)
            .ToList();
        slots.Should().HaveCount(2, "two var assignments emit two LoadFnCached");
        slots.Distinct().Should().HaveCount(1, "same fn name shares one slot");
    }

    [Fact]
    public void Different_Methods_Distinct_Slots()
    {
        var m = GenModule("""
            namespace Demo;
            public delegate int IntFn();
            int A() { return 1; }
            int B() { return 2; }
            void Main() {
                IntFn x = A;
                IntFn y = B;
            }
            """);
        var slots = All(FindFn(m, ".Main"))
            .OfType<LoadFnCachedInstr>()
            .Select(i => i.SlotId)
            .ToList();
        slots.Distinct().Should().HaveCount(2, "two distinct fns get two distinct slots");
    }

    [Fact]
    public void Module_FuncRefCacheSlotCount_Matches_Allocated()
    {
        var m = GenModule("""
            namespace Demo;
            public delegate int IntFn();
            int A() { return 1; }
            int B() { return 2; }
            void Main() {
                IntFn x = A;
                IntFn y = B;
            }
            """);
        m.FuncRefCacheSlotCount.Should().Be(2);
    }

    [Fact]
    public void Lambda_Literal_Still_Emits_LoadFn()
    {
        var m = GenModule("""
            namespace Demo;
            public delegate int IntFn(int x);
            void Main() {
                IntFn sq = (int x) => x * x;
                var r = sq(5);
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        // lifted lambda goes through LoadFn, not LoadFnCached（design Decision 6）
        instrs.OfType<LoadFnInstr>().Any().Should().BeTrue(
            "lambda literal should emit LoadFn for lifted name (no slot caching)");
        instrs.OfType<LoadFnCachedInstr>().Any().Should().BeFalse(
            "no method group conversion → no LoadFnCached");
    }
}
