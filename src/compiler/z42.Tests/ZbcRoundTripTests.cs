using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;
using Z42.Semantics.Codegen;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Tests for binary zbc format: write → read → compare.
/// We compare IrModules via their JSON representation for structural equality.
/// </summary>
public class ZbcRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // ── Dependency index + imported TSIG (loads once from artifacts/z42/libs/) ────

    private static readonly DependencyIndex DepIndex = LoadDepIndex();
    private static readonly ImportedSymbols? Imported = LoadImported();

    private static DependencyIndex LoadDepIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate)) return PackageCompiler.BuildDepIndex([candidate]);
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
                var cache = new TsigCache();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IrModule Compile(string source)
    {
        var tokens = new Lexer(source, "<test>").Tokenize();
        var diags  = new DiagnosticBag();
        CompilationUnit cu;
        try { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            throw new InvalidOperationException(
                $"Parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
        }
        var model = new TypeChecker(diags, depIndex: DepIndex).Check(cu, Imported);
        if (diags.HasErrors)
        {
            var sw = new System.IO.StringWriter();
            diags.PrintAll(sw);
            throw new InvalidOperationException("Type errors:\n" + sw);
        }
        return new IrGen(DepIndex, semanticModel: model).Generate(cu);
    }

    private static string ToJson(IrModule m) => JsonSerializer.Serialize(m, JsonOpts);

    private static IrModule RoundTrip(IrModule original)
    {
        byte[]   bytes = ZbcWriter.Write(original);
        IrModule read  = ZbcReader.Read(bytes);
        return read;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_HelloWorld()
    {
        const string src = """
            namespace hello;

            void Main() {
                Console.WriteLine("Hello, World!");
            }
            """;

        var original = Compile(src);
        var bytes    = ZbcWriter.Write(original);

        bytes.Should().NotBeEmpty();
        bytes[0].Should().Be((byte)'Z');
        bytes[1].Should().Be((byte)'B');
        bytes[2].Should().Be((byte)'C');
        bytes[3].Should().Be(0);

        var restored = ZbcReader.Read(bytes);

        restored.Name.Should().Be(original.Name);
        restored.Functions.Should().HaveSameCount(original.Functions);
        restored.Classes.Should().HaveSameCount(original.Classes);

        // Compare function names and block counts
        for (int i = 0; i < original.Functions.Count; i++)
        {
            restored.Functions[i].Name.Should().Be(original.Functions[i].Name);
            restored.Functions[i].ParamCount.Should().Be(original.Functions[i].ParamCount);
            restored.Functions[i].ExecMode.Should().Be(original.Functions[i].ExecMode);
            restored.Functions[i].Blocks.Should().HaveSameCount(original.Functions[i].Blocks);
        }
    }

    [Fact]
    public void RoundTrip_Arithmetic()
    {
        const string src = """
            namespace math;

            int Add(int a, int b) {
                return a + b;
            }

            void Main() {
                var r = Add(3, 4);
            }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        restored.Functions.Should().HaveSameCount(original.Functions);
        for (int i = 0; i < original.Functions.Count; i++)
        {
            var orig = original.Functions[i];
            var rest = restored.Functions[i];
            rest.Name.Should().Be(orig.Name);
            rest.ParamCount.Should().Be(orig.ParamCount);
            rest.Blocks.Sum(b => b.Instructions.Count)
                .Should().Be(orig.Blocks.Sum(b => b.Instructions.Count),
                    "instruction counts should match");
        }
    }

    [Fact]
    public void RoundTrip_StringConstants()
    {
        const string src = """
            namespace greet;

            void Main() {
                var s = "Hello";
                Console.WriteLine(s);
            }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        // String pool round-trip: all strings should survive
        foreach (var s in original.StringPool)
            restored.StringPool.Should().Contain(s);
    }

    [Fact]
    public void RoundTrip_IfElse()
    {
        const string src = """
            namespace cond;

            void Main() {
                var x = 10;
                if (x > 5) {
                    Console.WriteLine("big");
                } else {
                    Console.WriteLine("small");
                }
            }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        // if/else generates multiple blocks — verify block structure preserved
        var origMain = original.Functions.First(f => f.Name.EndsWith("Main"));
        var restMain = restored.Functions.First(f => f.Name.EndsWith("Main"));
        restMain.Blocks.Should().HaveSameCount(origMain.Blocks);
    }

    [Fact]
    public void ZasmWriter_ProducesReadableOutput()
    {
        const string src = """
            namespace demo;

            void Main() {
                Console.WriteLine("hi");
            }
            """;

        var module = Compile(src);
        string zasm = ZasmWriter.Write(module);

        zasm.Should().Contain(".module");
        zasm.Should().Contain(".func");
        zasm.Should().Contain(".block");
        zasm.Should().Contain("ret");
    }

    [Fact]
    public void ZbcWriter_MagicBytesCorrect()
    {
        var module = new IrModule(
            Name       : "test",
            StringPool : [],
            Classes    : [],
            Functions  : [
                new IrFunction(
                    Name       : "test.Main",
                    ParamCount : 0,
                    RetType    : "void",
                    ExecMode   : "Interp",
                    Blocks     : [new IrBlock("entry", [], new RetTerm(null))])
            ]
        );

        byte[] bytes = ZbcWriter.Write(module);
        bytes[0].Should().Be((byte)'Z');
        bytes[1].Should().Be((byte)'B');
        bytes[2].Should().Be((byte)'C');
        bytes[3].Should().Be(0);
        // v0.2 format: version is two u16-LE fields
        BitConverter.ToUInt16(bytes, 4).Should().Be(ZbcWriter.VersionMajor); // major = 0
        BitConverter.ToUInt16(bytes, 6).Should().Be(ZbcWriter.VersionMinor); // minor = 2
        // flags = 0 (full mode, no debug)
        BitConverter.ToUInt16(bytes, 8).Should().Be(0);
    }

    [Fact]
    public void ZbcReader_ReadNamespace_ReturnsCorrectNamespace()
    {
        const string src = """
            namespace Demo.Greet;

            void Main() { }
            """;

        var module = Compile(src);
        byte[] bytes = ZbcWriter.Write(module);

        ZbcReader.ReadNamespace(bytes).Should().Be("Demo.Greet");
    }

    [Fact]
    public void ZbcWriter_StrippedMode_FlagsSet()
    {
        const string src = """
            namespace Demo;

            void Main() { }
            """;

        var module   = Compile(src);
        byte[] full  = ZbcWriter.Write(module, ZbcFlags.None);
        byte[] stripped = ZbcWriter.Write(module, ZbcFlags.Stripped);

        // Flags byte in header
        ((ZbcFlags)BitConverter.ToUInt16(full,     8)).Should().Be(ZbcFlags.None);
        ((ZbcFlags)BitConverter.ToUInt16(stripped, 8)).Should().Be(ZbcFlags.Stripped);

        // Stripped should be smaller (no SIGS/IMPT/EXPT/STRS)
        stripped.Length.Should().BeLessThan(full.Length);

        // Namespace still readable from stripped
        ZbcReader.ReadNamespace(stripped).Should().Be("Demo");
    }

    [Fact]
    public void ZbcWriter_StrippedMode_ContentStable()
    {
        const string src = """
            namespace Stable;

            int Add(int a, int b) { return a + b; }
            void Main() { var r = Add(1, 2); }
            """;

        var module = Compile(src);
        byte[] first  = ZbcWriter.Write(module, ZbcFlags.Stripped);
        byte[] second = ZbcWriter.Write(module, ZbcFlags.Stripped);

        first.Should().Equal(second, "same source must produce identical stripped zbc bytes");
    }

    // ── Debug line table round-trip ─────────────────────────────────────────

    [Fact]
    public void LineTable_IsGeneratedByEmitter()
    {
        const string src = """
            namespace demo;

            void Main() {
                int x = 10;
                int y = 20;
                int z = x + y;
            }
            """;

        var module = Compile(src);
        var main   = module.Functions.Should().ContainSingle(f => f.Name.EndsWith("Main")).Subject;

        main.LineTable.Should().NotBeNull();
        main.LineTable!.Count.Should().BeGreaterThanOrEqualTo(3,
            "three statements should produce three line-table entries");

        // Lines should be strictly monotonically increasing (each stmt emits its own line)
        var lines = main.LineTable.Select(e => e.Line).ToList();
        lines.Should().BeInAscendingOrder();
    }

    [Fact]
    public void LineTable_SurvivesBinaryRoundTrip()
    {
        const string src = """
            namespace demo;

            int Compute() {
                int a = 1;
                int b = 2;
                return a + b;
            }

            void Main() {
                var r = Compute();
            }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        for (int i = 0; i < original.Functions.Count; i++)
        {
            var o = original.Functions[i];
            var r = restored.Functions[i];
            if (o.LineTable is null)
            {
                r.LineTable.Should().BeNull($"function {o.Name} had no line table");
                continue;
            }
            r.LineTable.Should().NotBeNull($"function {o.Name} must preserve line table");
            r.LineTable!.Count.Should().Be(o.LineTable.Count,
                $"function {o.Name} line table entry count must round-trip");
            for (int k = 0; k < o.LineTable.Count; k++)
            {
                r.LineTable[k].BlockIdx.Should().Be(o.LineTable[k].BlockIdx);
                r.LineTable[k].InstrIdx.Should().Be(o.LineTable[k].InstrIdx);
                r.LineTable[k].Line.Should().Be(o.LineTable[k].Line);
            }
        }
    }

    // ── Generics (L3-G1) round-trip ──────────────────────────────────────────

    [Fact]
    public void TypeParams_OnGenericClass_SurvivesBinaryRoundTrip()
    {
        const string src = """
            namespace demo;

            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
                T Get() { return this.value; }
            }

            void Main() { var b = new Box(42); }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        var origBox = original.Classes.Single(c => c.Name.EndsWith("Box"));
        var restBox = restored.Classes.Single(c => c.Name.EndsWith("Box"));
        restBox.TypeParams.Should().NotBeNull();
        restBox.TypeParams!.Should().Equal(origBox.TypeParams!);
    }

    [Fact]
    public void TypeParams_OnGenericFunction_SurvivesBinaryRoundTrip()
    {
        const string src = """
            namespace demo;

            T Identity<T>(T x) { return x; }

            void Main() { var r = Identity(42); }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        var origFn = original.Functions.Single(f => f.Name.EndsWith("Identity"));
        var restFn = restored.Functions.Single(f => f.Name.EndsWith("Identity"));
        restFn.TypeParams.Should().NotBeNull();
        restFn.TypeParams!.Should().Equal(origFn.TypeParams!);
    }

    [Fact]
    public void LocalVarTable_SurvivesBinaryRoundTrip()
    {
        const string src = """
            namespace demo;

            int Add(int a, int b) {
                var sum = a + b;
                return sum;
            }

            void Main() {
                var result = Add(3, 4);
            }
            """;

        var original = Compile(src);
        var restored = RoundTrip(original);

        for (int i = 0; i < original.Functions.Count; i++)
        {
            var o = original.Functions[i];
            var r = restored.Functions[i];
            if (o.LocalVarTable is null)
            {
                r.LocalVarTable.Should().BeNull($"function {o.Name} had no local var table");
                continue;
            }
            r.LocalVarTable.Should().NotBeNull($"function {o.Name} must preserve local var table");
            r.LocalVarTable!.Count.Should().Be(o.LocalVarTable.Count,
                $"function {o.Name} local var count must round-trip");
            for (int k = 0; k < o.LocalVarTable.Count; k++)
            {
                r.LocalVarTable[k].Name.Should().Be(o.LocalVarTable[k].Name);
                r.LocalVarTable[k].RegId.Should().Be(o.LocalVarTable[k].RegId);
            }
        }
    }
}
