using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Symbols;

/// <summary>
/// Base contract for every declared symbol in a compilation — Roslyn
/// `Microsoft.CodeAnalysis.ISymbol` parallel.
///
/// review.md F2.2 Phase 1 (2026-06-03, add-isymbol-base-phase1) introduces
/// this as the parent of the existing <see cref="IMemberSymbol"/> hierarchy
/// (which already shipped through split-symbol-from-type). The Phase 1
/// surface is intentionally tight — just the properties that Roslyn's
/// equivalents call "always-present on every symbol":
///
/// <list type="bullet">
///   <item><see cref="Name"/> — un-mangled source identifier.</item>
///   <item><see cref="Kind"/> — discriminator for downcasting.</item>
///   <item><see cref="DeclarationSpan"/> — primary source location.</item>
///   <item><see cref="Visibility"/> — public / private / internal /
///     protected.</item>
/// </list>
///
/// Properties Roslyn exposes that **Phase 1 deliberately defers**:
///
/// <list type="bullet">
///   <item><c>ContainingSymbol</c> — needs <c>ITypeSymbol</c> as a peer
///     interface, which Phase 2 introduces alongside
///     <c>INamedTypeSymbol</c>.</item>
///   <item><c>ContainingNamespace</c> — needs <see cref="SymbolKind.Namespace"/>
///     to be a concrete symbol, Phase 2.</item>
///   <item><c>SymbolId</c> — stable cross-compilation identity; needs the
///     metadata side of F2.1 Phase 2 (multi-Compilation refs) to be
///     meaningful.</item>
///   <item><c>OriginalDefinition</c> / <c>Construct</c> — generic
///     instantiation tracking; Phase 4 with
///     <see cref="SymbolKind.TypeParameter"/>.</item>
/// </list>
///
/// # Equality
///
/// Symbol equality is by reference today (the sealed classes implement
/// their own <c>Equals</c> based on <c>(ContainingType.Name, Name,
/// Signature)</c> — kept unchanged in Phase 1). Phase 2 introduces an
/// explicit <c>SymbolEqualityComparer</c> for callers that want a
/// stronger contract.
/// </summary>
public interface ISymbol
{
    /// Un-mangled source identifier (e.g. <c>"Foo"</c> for class
    /// <c>Foo</c>; <c>"Bar$2"</c> for an overloaded method's disambiguated
    /// key).
    string Name { get; }

    /// Variant tag for downcasting. Matches the concrete implementation
    /// type one-for-one in Phase 1; future kinds (Class / Local / etc.)
    /// land in Phase 2-4.
    SymbolKind Kind { get; }

    /// Primary source location of the declaration. For local-decl symbols
    /// this matches the AST node's <see cref="Span"/>; for imported
    /// symbols it's the span recorded in the dependency's TSIG (may be
    /// <c>default(Span)</c> when the import path lost source info).
    Span DeclarationSpan { get; }

    /// Access modifier as declared.
    Visibility Visibility { get; }
}
