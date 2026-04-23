using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.Project;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// L3-G3d: cross-zpkg `where`-clause propagation through the TSIG section.
///
/// Verifies that (1) ZpkgWriter → ZpkgReader round-trips constraint metadata,
/// (2) ImportedSymbolLoader surfaces it to the consumer, and (3) TypeChecker
/// rejects `new ImportedGeneric<T>()` with a type arg that doesn't satisfy the
/// declared constraints.
/// </summary>
public sealed class TsigConstraintsTests
{
    /// Build a minimal `Lib.Box<T> where T: IEquatable` zpkg and verify the
    /// constraint block round-trips through ZpkgWriter + ZpkgReader.
    [Fact]
    public void Constraints_RoundTrip_ThroughZpkgBinary()
    {
        var constraint = new ExportedTypeParamConstraint(
            "T",
            Interfaces: ["IEquatable"],
            BaseClass: null,
            TypeParamRef: null,
            RequiresClass: false,
            RequiresStruct: false);
        var cls = new ExportedClassDef(
            Name: "Box", BaseClass: null,
            IsAbstract: false, IsSealed: false, IsStatic: false,
            Fields: [], Methods: [],
            Interfaces: [],
            TypeParams: ["T"],
            TypeParamConstraints: [constraint]);
        var module = new ExportedModule("Demo.Lib", [cls], [], [], []);

        var zpkg = new ZpkgFile(
            Name: "demo", Version: "0.1.0", Kind: ZpkgKind.Lib, Mode: ZpkgMode.Packed,
            Namespaces: ["Demo.Lib"],
            Exports: [], Dependencies: [], Files: [], Modules: [],
            Entry: null,
            ExportedModules: [module]);

        var bytes   = ZpkgWriter.Write(zpkg);
        var read    = ZpkgReader.ReadTsig(bytes);
        var clsRead = read.Single().Classes.Single();

        clsRead.TypeParamConstraints.Should().NotBeNull();
        clsRead.TypeParamConstraints![0].TypeParam.Should().Be("T");
        clsRead.TypeParamConstraints[0].Interfaces.Should().ContainSingle().Which.Should().Be("IEquatable");
        clsRead.TypeParamConstraints[0].RequiresClass.Should().BeFalse();
    }

    /// End-to-end: imported generic class with `where T: IEquatable<T>`;
    /// consumer code that tries to instantiate with a non-conforming type
    /// must produce a TypeMismatch diagnostic.
    [Fact]
    public void ImportedConstraint_RejectsNonConformingTypeArg()
    {
        // Build synthetic imported symbols: `Box<T> where T: IEquatable`.
        var ieq = new Z42InterfaceType("IEquatable",
            new Dictionary<string, Z42FuncType> {
                ["Equals"] = new Z42FuncType([], Z42Type.Bool)
            });
        var box = new Z42ClassType(
            Name: "Box",
            Fields: new Dictionary<string, Z42Type>(),
            Methods: new Dictionary<string, Z42FuncType>(),
            StaticFields: new Dictionary<string, Z42Type>(),
            StaticMethods: new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName: null,
            TypeParams: new List<string> { "T" });
        var imported = new ImportedSymbols(
            Classes: new() { ["Box"] = box },
            Functions: new(),
            Interfaces: new() { ["IEquatable"] = ieq },
            EnumConstants: new(),
            EnumTypes: [],
            ClassNamespaces: new() { ["Box"] = "Demo.Lib" },
            ClassConstraints: new()
            {
                ["Box"] = [
                    new ExportedTypeParamConstraint(
                        TypeParam: "T",
                        Interfaces: ["IEquatable"],
                        BaseClass: null,
                        TypeParamRef: null,
                        RequiresClass: false,
                        RequiresStruct: false)
                ]
            },
            FuncConstraints: null);

        // Consumer source: NoEq doesn't implement IEquatable; `new Box<NoEq>()` must fail.
        const string src = @"
class NoEq { int x; NoEq() { this.x = 0; } }
void Main() {
    var b = new Box<NoEq>();
}";
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported);

        diags.HasErrors.Should().BeTrue();
        var sw = new StringWriter();
        diags.PrintAll(sw);
        sw.ToString().Should().Contain("IEquatable");
        sw.ToString().Should().Contain("NoEq");
    }

    /// Positive case: the type arg DOES satisfy the imported constraint → no errors.
    [Fact]
    public void ImportedConstraint_AcceptsConformingTypeArg()
    {
        var ieq = new Z42InterfaceType("IEquatable",
            new Dictionary<string, Z42FuncType> {
                ["Equals"] = new Z42FuncType([], Z42Type.Bool)
            });
        var box = new Z42ClassType(
            Name: "Box",
            Fields: new Dictionary<string, Z42Type>(),
            Methods: new Dictionary<string, Z42FuncType>(),
            StaticFields: new Dictionary<string, Z42Type>(),
            StaticMethods: new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName: null,
            TypeParams: new List<string> { "T" });
        // L3-G4b primitive-as-struct: stdlib normally declares `struct int : IEquatable<int>`;
        // synthetic imports must carry the same interface list so data-driven
        // `PrimitiveImplementsInterface` accepts `int` as conforming.
        var intStruct = new Z42ClassType(
            Name: "int",
            Fields: new Dictionary<string, Z42Type>(),
            Methods: new Dictionary<string, Z42FuncType>(),
            StaticFields: new Dictionary<string, Z42Type>(),
            StaticMethods: new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName: null,
            IsStruct: true);
        var imported = new ImportedSymbols(
            Classes: new() { ["Box"] = box, ["int"] = intStruct },
            Functions: new(),
            Interfaces: new() { ["IEquatable"] = ieq },
            EnumConstants: new(),
            EnumTypes: [],
            ClassNamespaces: new() { ["Box"] = "Demo.Lib", ["int"] = "Std" },
            ClassConstraints: new()
            {
                ["Box"] = [
                    new ExportedTypeParamConstraint(
                        TypeParam: "T",
                        Interfaces: ["IEquatable"],
                        BaseClass: null,
                        TypeParamRef: null,
                        RequiresClass: false,
                        RequiresStruct: false)
                ]
            },
            FuncConstraints: null,
            ClassInterfaces: new() { ["int"] = ["IEquatable"] });

        // int implements IEquatable via the stdlib-declared struct (imported above).
        const string src = @"void Main() { var b = new Box<int>(); }";
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu, imported);

        diags.HasErrors.Should().BeFalse();
    }
}
