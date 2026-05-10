using Z42.Core.Text;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Symbols;

/// <summary>
/// A field declaration symbol. Mirrors IMethodSymbol shape; same imported /
/// local distinction via nullable Decl.
/// </summary>
public interface IFieldSymbol : IMemberSymbol
{
    Z42Type Type { get; }
    bool IsStatic { get; }
    bool IsEvent { get; }
    FieldDecl? Decl { get; }
}

/// <summary>
/// Default IFieldSymbol implementation.
/// Equality: `(ContainingType.Name, Name, Type)`.
/// </summary>
public sealed class FieldSymbol : IFieldSymbol
{
    public string Name { get; }
    public Span Span { get; }
    public Visibility Visibility { get; }
    public Z42Type? ContainingType { get; }
    public Z42Type Type { get; }
    public bool IsStatic { get; }
    public bool IsEvent { get; }
    public FieldDecl? Decl { get; }

    public FieldSymbol(
        string name,
        Z42Type? containingType,
        Z42Type type,
        bool isStatic,
        Span span,
        Visibility visibility,
        bool isEvent = false,
        FieldDecl? decl = null)
    {
        Name = name;
        ContainingType = containingType;
        Type = type;
        IsStatic = isStatic;
        Span = span;
        Visibility = visibility;
        IsEvent = isEvent;
        Decl = decl;
    }

    private static string NameOf(Z42Type? t) => t switch
    {
        Z42ClassType ct      => ct.Name,
        Z42InterfaceType it  => it.Name,
        null                 => "",
        _                    => t.ToString() ?? "",
    };

    public override bool Equals(object? obj) =>
        obj is FieldSymbol o
        && Name == o.Name
        && NameOf(ContainingType) == NameOf(o.ContainingType)
        && Type.Equals(o.Type);

    public override int GetHashCode() =>
        HashCode.Combine(Name, NameOf(ContainingType), Type);

    public override string ToString() =>
        $"{NameOf(ContainingType)}.{Name}: {Type}";
}
