using Z42.Core.Text;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Symbols;

/// <summary>
/// Base interface for all member symbols (methods, fields, properties — though
/// only IMethodSymbol/IFieldSymbol are concrete in the initial split-symbol-from-type
/// landing).
///
/// Symbol layer separates **declaration identity** (this interface) from **type
/// identity** (Z42Type). A `Z42ClassType` carries class shape (Name, TypeParams,
/// BaseClassName); IMemberSymbol carries the per-declaration information
/// (Span, Visibility, optional back-pointer to the source FunctionDecl/FieldDecl).
///
/// 设计规则（不变量，参见 docs/spec/archive/.../split-symbol-from-type/design.md）：
/// 1. Symbol 持有 ContainingType 反向引用（class / interface / null=top-level）
/// 2. Z42ClassType.Methods/Fields 字典值是 IMethodSymbol/IFieldSymbol（不是 Z42FuncType/Z42Type 直接签名）
/// 3. 本地路径 Decl 非空；imported 路径 Decl 为 null
/// 4. Modifiers / Span 字段是 single source of truth；Decl 仅作 back-pointer 用于反射 / 工具链
/// </summary>
public interface IMemberSymbol : ISymbol
{
    /// Member name (un-mangled source identifier; for overloaded methods this is
    /// the disambiguated key, e.g. `Foo$2`).
    new string Name { get; }

    /// Source location of the declaration (FunctionDecl.Span / FieldDecl.Span
    /// for local; from imported metadata for cross-zpkg, may be Span.Empty).
    Span Span { get; }

    new Visibility Visibility { get; }

    /// Containing type — `Z42ClassType` for class/struct members, `Z42InterfaceType`
    /// for interface members, `null` for top-level free functions / fields.
    Z42Type? ContainingType { get; }

    // ── ISymbol bridging (add-isymbol-base-phase1, 2026-06-03) ─────────────
    //
    // The base `ISymbol` interface declares `Name` / `DeclarationSpan` /
    // `Visibility`. IMemberSymbol re-declares the matching pair (`Name`,
    // `Visibility`) with `new` so the existing implementation classes
    // (`MethodSymbol`, `FieldSymbol`) need no rename. `DeclarationSpan`
    // maps to `Span` via a default getter — same property, different name
    // because Roslyn calls it `Locations[0]` / `DeclaringSyntaxReferences`
    // and we don't want to drag `Span` into ISymbol's vocabulary for
    // future non-source-backed symbols (e.g. synthesized closure env
    // classes have no `Span`).

    Span ISymbol.DeclarationSpan => Span;
}
