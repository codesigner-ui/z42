using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Tests;

/// <summary>
/// Tests for binary z42bc format: write → read → compare.
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
        new TypeChecker(diags).Check(cu);
        if (diags.HasErrors)
        {
            var sw = new System.IO.StringWriter();
            diags.PrintAll(sw);
            throw new InvalidOperationException("Type errors:\n" + sw);
        }
        return new IrGen().Generate(cu);
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
}
