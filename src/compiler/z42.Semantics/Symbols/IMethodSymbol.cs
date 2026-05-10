using Z42.Core.Text;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Symbols;

/// <summary>
/// A method declaration symbol — fields are immutable; modifier convenience
/// getters are derived from `Modifiers` flag (single source of truth).
///
/// Local-decl path: `Decl` non-null (FunctionDecl from CompilationUnit AST).
/// Imported-decl path: `Decl == null`; `Modifiers` / `Span` / `Signature` come from TSIG.
/// Interface-abstract path: `Decl == null` (no FunctionDecl backing pure abstract sig).
/// </summary>
public interface IMethodSymbol : IMemberSymbol
{
    Z42FuncType Signature { get; }
    FunctionModifiers Modifiers { get; }
    FunctionDecl? Decl { get; }
    IReadOnlyList<TestAttribute>? TestAttributes { get; }

    bool IsStatic   => Modifiers.HasFlag(FunctionModifiers.Static);
    bool IsVirtual  => Modifiers.HasFlag(FunctionModifiers.Virtual);
    bool IsOverride => Modifiers.HasFlag(FunctionModifiers.Override);
    bool IsAbstract => Modifiers.HasFlag(FunctionModifiers.Abstract);
    bool IsExtern   => Modifiers.HasFlag(FunctionModifiers.Extern);
}

/// <summary>
/// Default IMethodSymbol implementation.
///
/// Equality: `(ContainingType.Name, Name, Signature)` — Decl / Modifiers /
/// TestAttributes are NOT in the equality contract because:
/// - Decl is a back-pointer (may be null for imported); two symbols for "same
///   method" can differ on Decl presence
/// - Modifiers: derived from Decl at construction; same method has same modifiers
/// - TestAttributes: same method has same attrs
///
/// Use sealed class (not record) to avoid cyclic Equals through ContainingType
/// (Z42ClassType holds IMethodSymbol holds Z42ClassType…).
/// </summary>
public sealed class MethodSymbol : IMethodSymbol
{
    public string Name { get; }
    public Span Span { get; }
    public Visibility Visibility { get; }
    /// Internally settable to support two-phase construction:
    /// SymbolCollector builds the symbol with containingType=null, then constructs
    /// Z42ClassType with these symbols, then fixes-up ContainingType post-construction.
    /// Resolves the chicken-and-egg between Z42ClassType.Methods (holds IMethodSymbol)
    /// and IMethodSymbol.ContainingType (holds Z42ClassType).
    public Z42Type? ContainingType { get; internal set; }
    public Z42FuncType Signature { get; }
    public FunctionModifiers Modifiers { get; }
    public FunctionDecl? Decl { get; }
    public IReadOnlyList<TestAttribute>? TestAttributes { get; }

    public MethodSymbol(
        string name,
        Z42Type? containingType,
        Z42FuncType signature,
        FunctionModifiers modifiers,
        Span span,
        Visibility visibility,
        FunctionDecl? decl = null,
        IReadOnlyList<TestAttribute>? testAttributes = null)
    {
        Name = name;
        ContainingType = containingType;
        Signature = signature;
        Modifiers = modifiers;
        Span = span;
        Visibility = visibility;
        Decl = decl;
        TestAttributes = testAttributes;
    }

    private static string NameOf(Z42Type? t) => t switch
    {
        Z42ClassType ct      => ct.Name,
        Z42InterfaceType it  => it.Name,
        null                 => "",
        _                    => t.ToString() ?? "",
    };

    public override bool Equals(object? obj) =>
        obj is MethodSymbol o
        && Name == o.Name
        && NameOf(ContainingType) == NameOf(o.ContainingType)
        && Signature.Equals(o.Signature);

    public override int GetHashCode() =>
        HashCode.Combine(Name, NameOf(ContainingType), Signature);

    public override string ToString() =>
        $"{NameOf(ContainingType)}.{Name}{Signature}";
}
