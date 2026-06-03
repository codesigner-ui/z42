namespace Z42.Semantics.Symbols;

/// <summary>
/// Discriminates the variant of an <see cref="ISymbol"/> at runtime — Roslyn
/// `Microsoft.CodeAnalysis.SymbolKind` parallel.
///
/// review.md F2.2 Phase 1 (2026-06-03, add-isymbol-base-phase1): Phase 1
/// populates `Method` + `Field` from the existing `MethodSymbol` /
/// `FieldSymbol`. The other members are forward-declared so consumers can
/// switch over every kind exhaustively today, even though their concrete
/// types land in Phase 2 (`Class`, `Interface`) / Phase 3 (`Local`,
/// `Parameter`).
/// </summary>
public enum SymbolKind
{
    /// A method (instance / static / abstract / extern). Populated by
    /// `MethodSymbol` in Phase 1.
    Method,

    /// A field (instance / static / event). Populated by `FieldSymbol` in
    /// Phase 1.
    Field,

    /// A user-defined class. Concrete `INamedTypeSymbol` lands in Phase 2.
    Class,

    /// A user-defined interface. Phase 2.
    Interface,

    /// A user-defined struct (primitive-as-struct included). Phase 2.
    Struct,

    /// A user-defined enum. Phase 2.
    Enum,

    /// A local variable inside a method / lambda body. Phase 3 with
    /// `ILocalSymbol`.
    Local,

    /// A method / lambda parameter. Phase 3 with `IParameterSymbol`.
    Parameter,

    /// A generic type parameter (`T` in `class Foo<T>` etc.). Phase 4 with
    /// `ITypeParameterSymbol`.
    TypeParameter,

    /// A namespace (declared via `namespace Foo.Bar;`). Phase 2.
    Namespace,
}
