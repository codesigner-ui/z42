using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Tests;

/// Unit tests for IrGen: verify that specific source constructs produce
/// the expected IR instructions and control-flow structure.
///
/// Tests are structural (instruction kinds, block counts, terminator types),
/// not register-number exact, to stay robust against minor codegen changes.
public sealed class IrGenTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly DependencyIndex DepIdx = LoadDepIdx();
    private static readonly ImportedSymbols? Imported = LoadImported();

    private static DependencyIndex LoadDepIdx()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate)) return BuildDepIdxFromDir(candidate);
            dir = dir.Parent;
        }
        return DependencyIndex.Empty;
    }

    private static ImportedSymbols? LoadImported()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate))
            {
                var cache = new Z42.Pipeline.TsigCache();
                foreach (var zpkg in Directory.EnumerateFiles(candidate, "*.zpkg"))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(zpkg);
                        foreach (var ns in ZpkgReader.ReadNamespaces(bytes))
                            cache.RegisterNamespace(ns, zpkg);
                    }
                    catch { }
                }
                var modules = cache.LoadAll();
                if (modules.Count == 0) return null;
                var allNs = modules.Select(m => m.Namespace).Distinct().ToList();
                return ImportedSymbolLoader.Load(modules, allNs);
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static DependencyIndex BuildDepIdxFromDir(string libsDir)
    {
        var modules = new List<(IrModule Module, string Namespace)>();
        foreach (var zpkgPath in Directory.EnumerateFiles(libsDir, "*.zpkg"))
        {
            try
            {
                var bytes = File.ReadAllBytes(zpkgPath);
                var meta  = ZpkgReader.ReadMeta(bytes);
                if (meta.Kind != ZpkgKind.Lib) continue;
                foreach (var (mod, ns) in ZpkgReader.ReadModules(bytes))
                    modules.Add((mod, ns));
            }
            catch { }
        }
        return DependencyIndex.Build(modules);
    }

    private static IrModule GenModule(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var model  = new TypeChecker(new DiagnosticBag()).Check(cu, Imported);
        return new IrGen(semanticModel: model).Generate(cu);
    }

    private static IrModule GenModuleWithDeps(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var model  = new TypeChecker(new DiagnosticBag(), depIndex: DepIdx).Check(cu, Imported);
        return new IrGen(DepIdx, semanticModel: model).Generate(cu);
    }

    /// Generate a module from a void Main() wrapping the given statements.
    private static IrFunction GenMain(string stmts)
        => GenModule($"void Main() {{ {stmts} }}").Functions[0];

    /// Flatten all instructions from all blocks of a function.
    private static List<IrInstr> All(IrFunction fn)
        => fn.Blocks.SelectMany(b => b.Instructions).ToList();

    private static void HasInstr<T>(List<IrInstr> instrs) where T : IrInstr =>
        instrs.Any(i => i is T)
              .Should().BeTrue(because: $"expected a {typeof(T).Name}");

    // ── Module-level ──────────────────────────────────────────────────────────

    [Fact]
    public void Module_Name_ComesFromNamespace()
    {
        var m = GenModule("namespace MyGame; void Main() {}");
        m.Name.Should().Be("MyGame");
    }

    [Fact]
    public void Module_DefaultName_IsMain()
    {
        var m = GenModule("void Main() {}");
        m.Name.Should().Be("main");
    }

    [Fact]
    public void StringPool_ContainsInterned_Strings()
    {
        var m = GenModule("void Main() { var s = \"hello\"; }");
        m.StringPool.Should().Contain("hello");
    }

    // ── Function metadata ─────────────────────────────────────────────────────

    [Fact]
    public void Function_VoidReturn_RetTypeIsVoid()
    {
        GenMain("").RetType.Should().Be("void");
    }

    [Fact]
    public void Function_IntReturn_RetTypeIsInt()
    {
        var fn = GenModule("int Foo() { return 42; }").Functions[0];
        fn.RetType.Should().Be("int");
    }

    [Fact]
    public void Function_ParamCount_IsCorrect()
    {
        var fn = GenModule("int Add(int a, int b) { return a + b; }").Functions[0];
        fn.ParamCount.Should().Be(2);
    }

    // ── Single-block return ───────────────────────────────────────────────────

    [Fact]
    public void EmptyVoidFunction_HasOneBlock_WithRetTermNull()
    {
        var fn = GenMain("");
        fn.Blocks.Should().HaveCount(1);
        fn.Blocks[0].Terminator.Should().BeOfType<RetTerm>()
            .Which.Reg.Should().BeNull();
    }

    [Fact]
    public void ReturnIntLiteral_RetTermCarriesRegister()
    {
        var fn = GenModule("int Foo() { return 1; }").Functions[0];
        fn.Blocks[0].Terminator.Should().BeOfType<RetTerm>()
            .Which.Reg.Should().NotBeNull();
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    [Fact]
    public void IntLiteral_EmitsConstI64()
    {
        var instrs = All(GenMain("var x = 99;"));
        instrs.Any(i => i is ConstI64Instr c && c.Val == 99)
              .Should().BeTrue(because: "expected ConstI64 with Val=99");
    }

    [Fact]
    public void BoolLiteral_True_EmitsConstBool()
    {
        var instrs = All(GenMain("var x = true;"));
        instrs.Any(i => i is ConstBoolInstr c && c.Val)
              .Should().BeTrue(because: "expected ConstBool with Val=true");
    }

    [Fact]
    public void NullLiteral_EmitsConstNull()
    {
        HasInstr<ConstNullInstr>(All(GenMain("var x = null;")));
    }

    [Fact]
    public void FloatLiteral_EmitsConstF64()
    {
        HasInstr<ConstF64Instr>(All(GenMain("var x = 3.14;")));
    }

    [Fact]
    public void StringLiteral_EmitsConstStr()
    {
        HasInstr<ConstStrInstr>(All(GenMain("var s = \"world\";")));
    }

    // ── Variable allocation (pure register-based) ─────────────────────────────

    [Fact]
    public void VarDecl_AllocatesRegister()
    {
        // Variables are now allocated registers on first assignment (pure register-based).
        // When a variable is used, we need at least a ConstI64 for the literal (VM unifies all ints as I64).
        var instrs = All(GenMain("var x = 42; var y = x;"));
        // Should have at least the ConstI64 for literal 42 and Copies for variable operations
        instrs.OfType<ConstI64Instr>().Should().NotBeEmpty(because: "expected ConstI64 for literal 42");
        instrs.OfType<CopyInstr>().Should().NotBeEmpty(because: "expected Copy for variable read");
    }

    [Fact]
    public void VarRead_UsesRegisterDirectly()
    {
        // Variables are now stored in registers. Reading a variable uses the register directly
        // without a separate Load instruction.
        var instrs = All(GenMain("var x = 1; var y = x;"));
        instrs.OfType<CopyInstr>().Should().NotBeEmpty(because: "expected Copy instruction for variable read");
    }

    // ── Arithmetic ────────────────────────────────────────────────────────────

    [Fact]
    public void Addition_EmitsAddInstr()
    {
        HasInstr<AddInstr>(All(GenMain("var r = 1 + 2;")));
    }

    [Fact]
    public void Subtraction_EmitsSubInstr()
    {
        HasInstr<SubInstr>(All(GenMain("var r = 5 - 3;")));
    }

    [Fact]
    public void Multiplication_EmitsMulInstr()
    {
        HasInstr<MulInstr>(All(GenMain("var r = 2 * 4;")));
    }

    [Fact]
    public void Division_EmitsDivInstr()
    {
        HasInstr<DivInstr>(All(GenMain("var r = 10 / 2;")));
    }

    [Fact]
    public void Modulo_EmitsRemInstr()
    {
        HasInstr<RemInstr>(All(GenMain("var r = 7 % 3;")));
    }

    // ── Comparison ────────────────────────────────────────────────────────────

    [Fact]
    public void EqComparison_EmitsEqInstr()
    {
        HasInstr<EqInstr>(All(GenMain("var r = 1 == 1;")));
    }

    [Fact]
    public void LtComparison_EmitsLtInstr()
    {
        HasInstr<LtInstr>(All(GenMain("var r = 1 < 2;")));
    }

    // ── Logical ───────────────────────────────────────────────────────────────

    [Fact]
    public void LogicalAnd_EmitsAndInstr()
    {
        HasInstr<AndInstr>(All(GenMain("var r = true && false;")));
    }

    [Fact]
    public void LogicalOr_EmitsOrInstr()
    {
        HasInstr<OrInstr>(All(GenMain("var r = true || false;")));
    }

    [Fact]
    public void LogicalNot_EmitsNotInstr()
    {
        HasInstr<NotInstr>(All(GenMain("var r = !true;")));
    }

    // ── Postfix ++ ────────────────────────────────────────────────────────────

    [Fact]
    public void PostfixIncrement_EmitsAddInstr()
    {
        HasInstr<AddInstr>(All(GenMain("var x = 0; x++;")));
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    [Fact]
    public void IfStmt_ProducesMultipleBlocks_WithBrCond()
    {
        var fn = GenMain("if (true) { var x = 1; }");
        fn.Blocks.Count.Should().BeGreaterThanOrEqualTo(3);
        fn.Blocks.Any(b => b.Terminator is BrCondTerm)
                 .Should().BeTrue(because: "expected a BrCondTerm terminator");
    }

    [Fact]
    public void IfElse_ProducesAtLeastFourBlocks()
    {
        var fn = GenMain("if (true) { var x = 1; } else { var y = 2; }");
        fn.Blocks.Count.Should().BeGreaterThanOrEqualTo(4);
        fn.Blocks.Any(b => b.Terminator is BrCondTerm)
                 .Should().BeTrue(because: "expected a BrCondTerm terminator");
    }

    [Fact]
    public void WhileLoop_ProducesAtLeastFourBlocks_WithBrCond()
    {
        var fn = GenMain("while (true) { var x = 1; }");
        fn.Blocks.Count.Should().BeGreaterThanOrEqualTo(4);
        fn.Blocks.Any(b => b.Terminator is BrCondTerm)
                 .Should().BeTrue(because: "expected a BrCondTerm terminator");
    }

    [Fact]
    public void ForLoop_ProducesAtLeastFiveBlocks()
    {
        // entry → cond → body → incr → end
        var fn = GenMain("for (var i = 0; i < 3; i++) {}");
        fn.Blocks.Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void BreakInWhile_EmitsBrTerminator()
    {
        var fn = GenMain("while (true) { break; }");
        fn.Blocks.Any(b => b.Terminator is BrTerm)
                 .Should().BeTrue(because: "break emits an unconditional Br");
    }

    // ── Function calls ────────────────────────────────────────────────────────

    [Fact]
    public void DirectCall_EmitsCallInstr()
    {
        var m = GenModule("int Foo() { return 1; } void Main() { var r = Foo(); }");
        var instrs = All(m.Functions.First(f => f.Name == "Main"));
        instrs.Any(i => i is CallInstr c && c.Func == "Foo")
              .Should().BeTrue(because: "expected CallInstr to Foo");
    }

    [Fact]
    public void DepCall_EmitsCallInstr()
    {
        // Console.WriteLine resolves to Std.IO.Console.WriteLine in stdlib
        var m = GenModuleWithDeps("void Main() { Console.WriteLine(\"hi\"); }");
        var instrs = All(m.Functions[0]);
        instrs.Any(i => i is CallInstr c && c.Func.StartsWith("Std.IO", StringComparison.Ordinal))
              .Should().BeTrue(because: "Console.WriteLine should emit a CallInstr to Std.IO stdlib");
    }

    // ── Arrays ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArrayCreate_EmitsArrayNewInstr()
    {
        HasInstr<ArrayNewInstr>(All(GenMain("int[] arr = new int[5];")));
    }

    [Fact]
    public void ArrayLiteral_EmitsArrayNewLitInstr()
    {
        HasInstr<ArrayNewLitInstr>(All(GenMain("int[] arr = new int[] { 1, 2, 3 };")));
    }

    [Fact]
    public void ArrayIndex_EmitsArrayGetInstr()
    {
        HasInstr<ArrayGetInstr>(All(GenMain("int[] arr = new int[3]; var x = arr[0];")));
    }

    [Fact]
    public void ArrayAssign_EmitsArraySetInstr()
    {
        HasInstr<ArraySetInstr>(All(GenMain("int[] arr = new int[3]; arr[0] = 99;")));
    }

    [Fact]
    public void ArrayLength_EmitsFieldGet()
    {
        // .Length on an array emits FieldGetInstr("Length"), not a __len builtin
        var instrs = All(GenMain("int[] arr = new int[3]; var n = arr.Length;"));
        instrs.Any(i => i is FieldGetInstr f && f.FieldName == "Length")
              .Should().BeTrue(because: "arr.Length should emit field_get \"Length\"");
    }

    // ── Foreach ───────────────────────────────────────────────────────────────

    [Fact]
    public void Foreach_EmitsArrayLen_And_ArrayGet()
    {
        var instrs = All(GenMain("int[] arr = new int[3]; foreach (var x in arr) {}"));
        HasInstr<ArrayLenInstr>(instrs);
        HasInstr<ArrayGetInstr>(instrs);
    }

    [Fact]
    public void Foreach_ProducesAtLeastFiveBlocks()
    {
        // entry → fe_cond → fe_body → fe_inc → fe_end
        var fn = GenMain("int[] arr = new int[3]; foreach (var x in arr) {}");
        fn.Blocks.Count.Should().BeGreaterThanOrEqualTo(5);
    }

    // ── String interpolation ──────────────────────────────────────────────────

    [Fact]
    public void InterpolatedString_EmitsToStr_And_StrConcat()
    {
        var instrs = All(GenMain("var x = 42; var s = $\"val={x}\";"));
        HasInstr<ToStrInstr>(instrs);
        HasInstr<StrConcatInstr>(instrs);
    }

    // ── Multiple functions ────────────────────────────────────────────────────

    [Fact]
    public void MultipleFunction_AllPresent()
    {
        var m = GenModule("int Add(int a, int b) { return a + b; } void Main() {}");
        m.Functions.Should().HaveCount(2);
        m.Functions.Select(f => f.Name).Should().Contain("Add").And.Contain("Main");
    }

    // ── Default parameter expansion ───────────────────────────────────────────

    [Fact]
    public void DefaultParam_Omitted_ExpandedAtCallSite()
    {
        // Greet("Alice") should expand to Greet("Alice", "Hello") in IR
        var fn = GenModule("""
            void Greet(string name, string greeting = "Hello") {}
            void Main() { Greet("Alice"); }
            """).Functions.Last(); // Main is last (Greet first)
        var call = All(fn).OfType<CallInstr>().First();
        call.Args.Should().HaveCount(2);
    }

    [Fact]
    public void DefaultParam_AllOmitted_BothExpanded()
    {
        var fn = GenModule("""
            void Reset(int x = 0, int y = 0) {}
            void Main() { Reset(); }
            """).Functions.Last();
        var call = All(fn).OfType<CallInstr>().First();
        call.Args.Should().HaveCount(2);
    }

    [Fact]
    public void DefaultParam_Explicit_NotExpanded()
    {
        // Greet("Alice", "Hi") — no default should be inserted
        var fn = GenModule("""
            void Greet(string name, string greeting = "Hello") {}
            void Main() { Greet("Alice", "Hi"); }
            """).Functions.Last();
        var call = All(fn).OfType<CallInstr>().First();
        call.Args.Should().HaveCount(2);
    }

    // ── extern / [Native] ─────────────────────────────────────────────────────

    [Fact]
    public void ExternMethod_EmitsSingleBuiltinInstr()
    {
        var module = GenModule("""
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            """);
        var fn = module.Functions.Should().ContainSingle().Subject;
        fn.Name.Should().EndWith(".WriteLine");
        var instrs = All(fn).ToList();
        instrs.OfType<BuiltinInstr>().Should().ContainSingle(b => b.Name == "__println");
        // args: [0] (param 0 = the 'value' string argument)
        var builtin = instrs.OfType<BuiltinInstr>().Single();
        builtin.Args.Select(a => a.Id).Should().Equal([0]);
    }

    [Fact]
    public void ExternMethod_VoidReturn_RetTermHasNoReg()
    {
        var module = GenModule("""
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            """);
        var fn = module.Functions.Single();
        fn.Blocks.Should().ContainSingle();
        fn.Blocks[0].Terminator.Should().BeOfType<RetTerm>()
            .Which.Reg.Should().BeNull();
    }

    [Fact]
    public void ExternMethod_WithReturnValue_RetTermHasReg()
    {
        var module = GenModule("""
            class Console {
                [Native("__readline")]
                public static extern string ReadLine();
            }
            """);
        var fn = module.Functions.Single();
        fn.Blocks[0].Terminator.Should().BeOfType<RetTerm>()
            .Which.Reg.Should().NotBeNull();
    }

    [Fact]
    public void ExternMethod_MultiParam_ArgsAreSequential()
    {
        var module = GenModule("""
            class Assert {
                [Native("__assert_eq")]
                public static extern void Equal(object expected, object actual);
            }
            """);
        var builtin = All(module.Functions.Single()).OfType<BuiltinInstr>().Single();
        builtin.Args.Select(a => a.Id).Should().Equal([0, 1]);
    }

    // ── Object protocol native stubs ──────────────────────────────────────────

    [Fact]
    public void ExternInstanceMethod_GetType_ThisIsArg0()
    {
        // GetType() is an instance method with no z42-level params; this = reg 0.
        var module = GenModule("""
            class Object {
                [Native("__obj_get_type")]
                public extern Type GetType();
            }
            class Type {}
            """);
        var fn = module.Functions.Should().ContainSingle(f => f.Name.EndsWith(".GetType")).Subject;
        var builtin = All(fn).OfType<BuiltinInstr>().Single();
        builtin.Name.Should().Be("__obj_get_type");
        builtin.Args.Select(a => a.Id).Should().Equal([0]); // reg 0 = this
    }

    [Fact]
    public void ExternStaticMethod_ReferenceEquals_TwoArgs()
    {
        // Static method with two params — no implicit this.
        var module = GenModule("""
            class Object {
                [Native("__obj_ref_eq")]
                public static extern bool ReferenceEquals(object a, object b);
            }
            """);
        var fn = module.Functions.Single();
        var builtin = All(fn).OfType<BuiltinInstr>().Single();
        builtin.Name.Should().Be("__obj_ref_eq");
        builtin.Args.Select(a => a.Id).Should().Equal([0, 1]);
    }

    [Fact]
    public void ExternVirtualInstanceMethod_GetHashCode_ThisIsArg0()
    {
        // virtual extern combines: emits native stub, this = reg 0.
        var module = GenModule("""
            class Object {
                [Native("__obj_hash_code")]
                public virtual extern int GetHashCode();
            }
            """);
        var fn = module.Functions.Should().ContainSingle(f => f.Name.EndsWith(".GetHashCode")).Subject;
        var builtin = All(fn).OfType<BuiltinInstr>().Single();
        builtin.Name.Should().Be("__obj_hash_code");
        builtin.Args.Select(a => a.Id).Should().Equal([0]);
    }
}
