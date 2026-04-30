using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Project;
using Z42.Semantics.Synthesis;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C11b — `NativeImportSynthesizer` end-to-end tests.
/// Each test parses a small z42 source, runs the synthesizer with an
/// `InMemoryManifestLocator`, then asserts the shape of the synthesized
/// `ClassDecl`(s) appended to `cu.Classes`.
public sealed class NativeImportSynthesizerTests
{
    private const string CounterManifest = """
        {
          "abi_version": 1,
          "module": "numz42",
          "version": "0.1.0",
          "library_name": "numz42",
          "types": [
            {
              "name": "Counter",
              "size": 8,
              "align": 8,
              "flags": ["sealed"],
              "fields": [],
              "methods": [
                {
                  "name": "Counter",
                  "kind": "ctor",
                  "symbol": "numz42_Counter_alloc",
                  "params": [],
                  "ret": "Self"
                },
                {
                  "name": "inc",
                  "kind": "method",
                  "symbol": "numz42_Counter_inc",
                  "params": [{ "name": "self", "type": "*mut Self" }],
                  "ret": "void"
                },
                {
                  "name": "get",
                  "kind": "method",
                  "symbol": "numz42_Counter_get",
                  "params": [{ "name": "self", "type": "*const Self" }],
                  "ret": "i64"
                },
                {
                  "name": "from_value",
                  "kind": "static",
                  "symbol": "numz42_Counter_from_value",
                  "params": [{ "name": "v", "type": "i64" }],
                  "ret": "Self"
                }
              ],
              "trait_impls": []
            }
          ]
        }
        """;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static CompilationUnit ParseOnly(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static InMemoryManifestLocator CounterLocator() =>
        new(new() { ["numz42"] = CounterManifest });

    // ── Happy paths ────────────────────────────────────────────────────────────

    [Fact]
    public void Synth_SingleImport_GeneratesClassDecl()
    {
        var cu = ParseOnly("import Counter from \"numz42\";");
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);

        cu.Classes.Should().ContainSingle();
        var cls = cu.Classes[0];
        cls.Name.Should().Be("Counter");
        cls.IsSealed.Should().BeTrue();
        cls.Visibility.Should().Be(Visibility.Internal);
        cls.Fields.Should().BeEmpty();
        cls.ClassNativeDefaults.Should().NotBeNull();
        cls.ClassNativeDefaults!.Lib.Should().Be("numz42");
        cls.ClassNativeDefaults!.TypeName.Should().Be("Counter");
        cls.Methods.Should().HaveCount(4);
    }

    [Fact]
    public void Synth_MethodTier1Entry_IsManifestSymbol()
    {
        var cu = ParseOnly("import Counter from \"numz42\";");
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);

        var inc = cu.Classes[0].Methods.Single(m => m.Name == "inc");
        inc.Tier1Binding.Should().NotBeNull();
        inc.Tier1Binding!.Entry.Should().Be("numz42_Counter_inc");
        inc.IsExtern.Should().BeTrue();
        inc.Visibility.Should().Be(Visibility.Public);
    }

    [Fact]
    public void Synth_Ctor_IsRecognizedAsConstructor()
    {
        var cu = ParseOnly("import Counter from \"numz42\";");
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);

        var ctor = cu.Classes[0].Methods.Single(m =>
            m.Name == "Counter" && m.Tier1Binding?.Entry == "numz42_Counter_alloc");
        ctor.ReturnType.Should().BeOfType<VoidType>();
        ctor.IsExtern.Should().BeTrue();
        ctor.IsStatic.Should().BeFalse();
    }

    [Fact]
    public void Synth_StaticMethod_HasStaticModifier()
    {
        var cu = ParseOnly("import Counter from \"numz42\";");
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);

        var fv = cu.Classes[0].Methods.Single(m => m.Name == "from_value");
        fv.IsStatic.Should().BeTrue();
        fv.IsExtern.Should().BeTrue();
    }

    [Fact]
    public void Synth_InstanceMethod_DropsReceiverFromParams()
    {
        var cu = ParseOnly("import Counter from \"numz42\";");
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);

        var inc = cu.Classes[0].Methods.Single(m => m.Name == "inc");
        // `*mut Self` self ⇒ moved out; the synthesized z42 method is parameterless.
        inc.Params.Should().BeEmpty();
    }

    [Fact]
    public void Synth_EmptyNativeImports_NoOp()
    {
        var cu = ParseOnly("class Foo { }");
        var classCountBefore = cu.Classes.Count;
        NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);
        cu.Classes.Should().HaveCount(classCountBefore);
    }

    [Fact]
    public void Synth_MultipleImports_OrderPreserved()
    {
        var twoTypes = """
            {
              "abi_version": 1,
              "module": "lib",
              "version": "0.1.0",
              "library_name": "lib",
              "types": [
                { "name": "A", "size": 8, "align": 8, "flags": [], "fields": [], "methods": [], "trait_impls": [] },
                { "name": "B", "size": 8, "align": 8, "flags": [], "fields": [], "methods": [], "trait_impls": [] }
              ]
            }
            """;
        var cu = ParseOnly("""
            import B from "lib";
            import A from "lib";
            """);
        var locator = new InMemoryManifestLocator(new() { ["lib"] = twoTypes });
        NativeImportSynthesizer.Run(cu, locator, sourceDir: null);

        cu.Classes.Select(c => c.Name).Should().Equal("B", "A");
    }

    // ── Error paths ────────────────────────────────────────────────────────────

    [Fact]
    public void Synth_TypeNotInManifest_ThrowsE0916()
    {
        var cu  = ParseOnly("import NoSuch from \"numz42\";");
        var act = () => NativeImportSynthesizer.Run(cu, CounterLocator(), sourceDir: null);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("NoSuch");
    }

    [Fact]
    public void Synth_ConflictingImports_DifferentLib_ThrowsE0916()
    {
        var cu = ParseOnly("""
            import Counter from "lib1";
            import Counter from "lib2";
            """);
        var locator = new InMemoryManifestLocator(new()
        {
            ["lib1"] = CounterManifest,
            ["lib2"] = CounterManifest,
        });
        var act = () => NativeImportSynthesizer.Run(cu, locator, sourceDir: null);
        act.Should().Throw<NativeImportException>()
           .Which.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
    }

    [Fact]
    public void Synth_UnsupportedSignature_ThrowsE0916()
    {
        var badManifest = """
            {
              "abi_version": 1,
              "module": "numz42",
              "version": "0.1.0",
              "library_name": "numz42",
              "types": [
                {
                  "name": "Bad",
                  "size": 8,
                  "align": 8,
                  "flags": [],
                  "fields": [],
                  "methods": [
                    {
                      "name": "name",
                      "kind": "method",
                      "symbol": "bad_name",
                      "params": [{ "name": "self", "type": "*const Self" }],
                      "ret": "*const c_char"
                    }
                  ],
                  "trait_impls": []
                }
              ]
            }
            """;
        var cu      = ParseOnly("import Bad from \"numz42\";");
        var locator = new InMemoryManifestLocator(new() { ["numz42"] = badManifest });
        var act     = () => NativeImportSynthesizer.Run(cu, locator, sourceDir: null);
        var ex      = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("c_char");
    }

    [Fact]
    public void Synth_ManifestNotFoundByLocator_ThrowsE0916()
    {
        var cu      = ParseOnly("import Counter from \"missing\";");
        var locator = new InMemoryManifestLocator(new());  // empty
        var act     = () => NativeImportSynthesizer.Run(cu, locator, sourceDir: null);
        act.Should().Throw<NativeImportException>()
           .Which.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
    }
}
