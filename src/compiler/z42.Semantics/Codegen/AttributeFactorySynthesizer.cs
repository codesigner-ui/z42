using System.Collections.Generic;
using System.Linq;
using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Codegen;

/// <summary>
/// add-attribute-reflection (C3) — AST-level pass that lowers each user
/// attribute application <c>[Foo(args)]</c> into a compiler-synthesized,
/// parameterless **factory function**:
///
/// <code>
/// [Route("/u", method: "POST")] class C { ... }
/// </code>
///
/// gains a top-level
///
/// <code>
/// public Route __attr$cls$C$0() { return new Route("/u", method: "POST"); }
/// </code>
///
/// and the application records that factory's name in
/// <see cref="AttributeApp.FactoryFunc"/>. Reflection (<c>GetCustomAttributes</c>)
/// calls the factory once and caches the result — so a *live* attribute
/// instance is produced with no runtime <c>Activator</c>/<c>Invoke</c>
/// machinery (all construction is statically known: known class, known
/// constructor, constant args baked into the factory body).
///
/// The factory body is an ordinary <c>new Foo(args)</c>, so the unchanged
/// TypeChecker resolves the constructor + named args + default values for free
/// (an unknown class or unmatched constructor surfaces as a normal type error
/// anchored at the attribute span). <c>AttributeValidator</c> adds the
/// attribute-specific checks (derives <c>Std.Attribute</c>; constant args).
///
/// Runs once, before TypeCheck, alongside <see cref="BenchmarkDesugar"/> in
/// <c>PipelineCore</c>. <c>$</c> is illegal in z42 identifiers, so the
/// synthesized names cannot collide with user code.
/// </summary>
public static class AttributeFactorySynthesizer
{
    private const string FactoryPrefix = "__attr$";

    /// <summary>Lower all attribute applications in <paramref name="cu"/>.
    /// Returns <paramref name="cu"/> unchanged when no user attribute exists
    /// (common case — avoids rebuilding the declaration lists).</summary>
    public static CompilationUnit Run(CompilationUnit cu)
    {
        bool any = cu.Functions.Any(HasAttrs)
                || cu.Classes.Any(c => HasAttrs(c.Attributes) || c.Methods.Any(HasAttrs));
        if (!any) return cu;

        var factories = new List<FunctionDecl>();

        var newFunctions = new List<FunctionDecl>(cu.Functions.Count);
        foreach (var fn in cu.Functions)
            newFunctions.Add(ProcessFunction(fn, "fn$" + fn.Name, factories));

        var newClasses = new List<ClassDecl>(cu.Classes.Count);
        foreach (var cls in cu.Classes)
            newClasses.Add(ProcessClass(cls, factories));

        // Synthesized factories become ordinary top-level functions.
        newFunctions.AddRange(factories);
        return cu with { Functions = newFunctions, Classes = newClasses };
    }

    private static bool HasAttrs(FunctionDecl fn) => HasAttrs(fn.Attributes);
    private static bool HasAttrs(List<AttributeApp>? attrs) => attrs is { Count: > 0 };

    private static ClassDecl ProcessClass(ClassDecl cls, List<FunctionDecl> factories)
    {
        var newClassAttrs = ProcessAttributes(cls.Attributes, "cls$" + cls.Name, factories);

        var newMethods = new List<FunctionDecl>(cls.Methods.Count);
        foreach (var m in cls.Methods)
            newMethods.Add(ProcessFunction(m, "mth$" + cls.Name + "$" + m.Name, factories));

        return cls with { Attributes = newClassAttrs, Methods = newMethods };
    }

    private static FunctionDecl ProcessFunction(FunctionDecl fn, string keyPrefix, List<FunctionDecl> factories)
    {
        var newAttrs = ProcessAttributes(fn.Attributes, keyPrefix, factories);
        return ReferenceEquals(newAttrs, fn.Attributes) ? fn : fn with { Attributes = newAttrs };
    }

    /// <summary>For each application, synthesize a factory function (appended to
    /// <paramref name="factories"/>) and return a new attribute list whose
    /// entries carry <see cref="AttributeApp.FactoryFunc"/>. Returns the input
    /// reference unchanged when there are no attributes.</summary>
    private static List<AttributeApp>? ProcessAttributes(
        List<AttributeApp>? attrs, string keyPrefix, List<FunctionDecl> factories)
    {
        if (!HasAttrs(attrs)) return attrs;
        var result = new List<AttributeApp>(attrs!.Count);
        for (int i = 0; i < attrs.Count; i++)
        {
            var a = attrs[i];
            string factoryName = FactoryPrefix + keyPrefix + "$" + i;
            factories.Add(SynthesizeFactory(a, factoryName));
            result.Add(a with { FactoryFunc = factoryName });
        }
        return result;
    }

    /// <summary>`public Foo &lt;factoryName&gt;() { return new Foo(args); }`.
    /// Spans point at the attribute application so constructor/type errors are
    /// anchored there.</summary>
    private static FunctionDecl SynthesizeFactory(AttributeApp a, string factoryName)
    {
        var s = a.Span;
        var ctorCall = new NewExpr(new NamedType(a.Name, s), a.Args, s);
        var body = new BlockStmt(new List<Stmt> { new ReturnStmt(ctorCall, s) }, s);
        return new FunctionDecl(
            Name:            factoryName,
            Params:          new List<Param>(),
            ReturnType:      new NamedType(a.Name, s),
            Body:            body,
            Visibility:      Visibility.Public,
            Modifiers:       FunctionModifiers.None,
            NativeIntrinsic: null,
            Span:            s);
    }
}
