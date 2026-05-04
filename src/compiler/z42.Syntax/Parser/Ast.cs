using Z42.Core.Text;
using Z42.Syntax.Lexer;

namespace Z42.Syntax.Parser;

// ── Access visibility ─────────────────────────────────────────────────────────

/// Access visibility for a declaration.
/// Phase 1 default: top-level and class members both default to Internal.
/// Only explicit `private` is enforced by the TypeChecker in Phase 1.
public enum Visibility { Private, Protected, Internal, Public }

// ── Root ──────────────────────────────────────────────────────────────────────

/// Top-level compilation unit produced by the parser.
public sealed record CompilationUnit(
    string? Namespace,
    List<string> Usings,
    List<ClassDecl> Classes,
    List<FunctionDecl> Functions,
    List<EnumDecl> Enums,
    List<InterfaceDecl> Interfaces,
    List<ImplDecl> Impls,
    Span Span,
    List<NativeTypeImport>? NativeImports = null,
    List<DelegateDecl>? Delegates = null);  // 2026-05-02 add-delegate-type

/// `import T from "lib";` — top-level native type import (spec C11a).
/// C11a only records the binding; C11b synthesizes the corresponding ClassDecl.
public sealed record NativeTypeImport(
    string Name,
    string LibName,
    Span Span);

// ── Extern impl block ─────────────────────────────────────────────────────────

/// `impl <TraitType> for <TargetType> { <methods> }` — Rust-style extern impl.
///
/// L3 extern impl core (Change 1): TargetType is a user class/struct or
/// primitive struct (int/double/bool/char), TraitType is an interface.
/// SymbolCollector merges trait into target's InterfaceTypes and methods into
/// target's Methods map. Methods must have a body (extern deferred).
public sealed record ImplDecl(
    TypeExpr TraitType,
    TypeExpr TargetType,
    List<FunctionDecl> Methods,
    Span Span);

// ── Generic constraints (L3-G2 / G2.5) ─────────────────────────────────────────

/// Flag constraints that live outside the type expression grammar.
/// (L3-G2.5 refvalue: class/struct; constructor: new(); future: notnull, ...)
[Flags]
public enum GenericConstraintKind
{
    None        = 0,
    Class       = 1 << 0,  // `where T: class`  — T must be a reference type
    Struct      = 1 << 1,  // `where T: struct` — T must be a value type
    Constructor = 1 << 2,  // `where T: new()`  — T must have a no-arg constructor
    Enum        = 1 << 3,  // `where T: enum`   — T must be an enum type
}

/// `where T: IFoo + IBar` or `where T: BaseClass` or `where T: class`
///
/// `TypeParam` names the constrained type parameter (must match one declared in
/// the enclosing TypeParams list); `Constraints` lists the class/interface
/// types combined with `+`; `Kinds` carries keyword flags (class/struct).
public sealed record GenericConstraint(
    string TypeParam,
    List<TypeExpr> Constraints,
    Span Span,
    GenericConstraintKind Kinds = GenericConstraintKind.None);

/// `where T: I [+ J]* [, K: I2]*`
///
/// Attaches to FunctionDecl, ClassDecl, and InterfaceDecl.
public sealed record WhereClause(
    List<GenericConstraint> Constraints,
    Span Span);

// ── Interface declaration ──────────────────────────────────────────────────────

/// `interface IShape { string Area(); int Width { get; } }`
/// `interface IComparable<T> { int CompareTo(T other); }`
public sealed record InterfaceDecl(
    string Name,
    Visibility Visibility,
    List<MethodSignature> Methods,
    Span Span,
    List<string>? TypeParams = null,
    WhereClause? Where = null);

/// A method signature for use in interface declarations.
///
/// L3 三档静态成员（方案 D1'）：
/// - `static abstract`：`IsStatic=true`, `IsVirtual=false`, `Body=null`（实现者必须 override）
/// - `static virtual`：`IsStatic=true`, `IsVirtual=true`,  `Body!=null`（可选 override，不 override 则用默认）
/// - `static`（无修饰）：`IsStatic=true`, `IsVirtual=false`, `Body!=null`（不可 override，sealed）
/// - instance abstract：`IsStatic=false`, `IsVirtual=false`, `Body=null`（原 interface 方法）
public sealed record MethodSignature(
    string Name,
    List<Param> Params,
    TypeExpr ReturnType,
    Span Span,
    bool IsStatic = false,
    bool IsVirtual = false,
    BlockStmt? Body = null);

// ── Enum declaration ──────────────────────────────────────────────────────────

/// `enum Color { Red, Green = 2, Blue }`
public sealed record EnumDecl(
    string Name,
    Visibility Visibility,
    List<EnumMember> Members,
    Span Span);

/// One member of an enum: `Green = 2`
public sealed record EnumMember(string Name, long? Value, Span Span);

// ── Class / struct declaration ────────────────────────────────────────────────

/// `class Foo { ... }` or `struct Foo { ... }` or `record Foo(...) { ... }`
public sealed record ClassDecl(
    string Name,
    bool IsStruct,
    bool IsRecord,
    bool IsAbstract,
    bool IsSealed,
    Visibility Visibility,
    string? BaseClass,              // null = no explicit base class
    List<TypeExpr> Interfaces,      // implemented interface types (may carry generic args, e.g. IEquatable<int>)
    List<FieldDecl> Fields,
    List<FunctionDecl> Methods,
    Span Span,
    List<string>? TypeParams = null,
    WhereClause? Where = null,   // L3-G2 generic constraints
    Tier1NativeBinding? ClassNativeDefaults = null,  // spec C9 — class-level [Native(lib=, type=)] shorthand
    List<DelegateDecl>? NestedDelegates = null);     // 2026-05-02 add-delegate-type

// ── Delegate declaration (2026-05-02 add-delegate-type) ──────────────────────

/// `delegate R Name<T1, T2>(T1 a, T2 b) where T : IFoo;`
///
/// 命名 callable 类型。语义上等价 `(T1, T2) -> R` 字面量类型；编译器把它解析
/// 为 `Z42FuncType`。支持顶层 + 嵌套（class body 内）+ 泛型 + where 约束。
/// 详见 `docs/design/delegates-events.md` §3 + spec/archive/.../add-delegate-type.
public sealed record DelegateDecl(
    string Name,
    Visibility Visibility,
    List<Param> Params,
    TypeExpr ReturnType,
    Span Span,
    List<string>? TypeParams = null,
    WhereClause? Where = null);

/// A field inside a class/struct: `int x;`
public sealed record FieldDecl(
    string Name,
    TypeExpr Type,
    Visibility Visibility,
    bool IsStatic,
    Expr? Initializer,
    Span Span,
    bool IsEvent = false);

// ── Function modifiers ────────────────────────────────────────────────────────

/// Modifier flags for function declarations.
/// Replaces scattered bool fields; mutually-exclusive combinations
/// (e.g. Abstract + Static) are validated at construction time.
[Flags]
public enum FunctionModifiers
{
    None     = 0,
    Static   = 1 << 0,
    Virtual  = 1 << 1,
    Override = 1 << 2,
    Abstract = 1 << 3,
    Extern   = 1 << 4,
}

// ── Function declaration ──────────────────────────────────────────────────────

/// Spec C6 — descriptor binding emitted by the `[Native(lib=, type=,
/// entry=)]` form. When non-null on a `FunctionDecl`, IR codegen emits
/// `CallNativeInstr` and `NativeIntrinsic` is null.
///
/// Spec C9 — fields are nullable so the parser can carry a *partial*
/// binding (e.g. only `Entry`) that gets stitched against a class-level
/// default at codegen time. After stitching the effective binding has
/// all three fields populated; type-check raises E0907 otherwise.
public sealed record Tier1NativeBinding(string? Lib, string? TypeName, string? Entry);

public sealed record FunctionDecl(
    string Name,
    List<Param> Params,
    TypeExpr ReturnType,
    BlockStmt Body,
    Visibility Visibility,
    FunctionModifiers Modifiers,
    string? NativeIntrinsic,
    Span Span,
    List<Expr>? BaseCtorArgs = null,  // non-null only on constructors with `: base(...)`
    List<string>? TypeParams = null,  // generic type parameters: <T>, <K,V>
    WhereClause? Where = null,        // L3-G2 generic constraints
    Tier1NativeBinding? Tier1Binding = null,  // spec C6 — Tier 1 dispatch binding
    List<TestAttribute>? TestAttributes = null)  // spec R1 — z42.test.* attributes (collected, validated in R4)
{
    // Convenience accessors — keep read sites concise
    public bool IsStatic   => Modifiers.HasFlag(FunctionModifiers.Static);
    public bool IsVirtual  => Modifiers.HasFlag(FunctionModifiers.Virtual);
    public bool IsOverride => Modifiers.HasFlag(FunctionModifiers.Override);
    public bool IsAbstract => Modifiers.HasFlag(FunctionModifiers.Abstract);
    public bool IsExtern   => Modifiers.HasFlag(FunctionModifiers.Extern);
}

public sealed record Param(string Name, TypeExpr Type, Expr? Default, Span Span);

// ── Test attributes (spec R1) ────────────────────────────────────────────────

/// <summary>One occurrence of a <c>z42.test.*</c> attribute on a function. The
/// parser collects these as-is; semantic validation (mutual exclusion,
/// signature checks) is done in R4.</summary>
public sealed record TestAttribute(
    /// <summary>Attribute name without brackets: "Test" / "Benchmark" / "Setup" /
    /// "Teardown" / "Ignore" / "Skip" / "ShouldThrow".</summary>
    string Name,
    /// <summary>Single type argument from `[Name&lt;TypeArg&gt;]` syntax (R4.B).
    /// Null when the attribute has no `&lt;...&gt;` clause. Currently only
    /// `[ShouldThrow&lt;E&gt;]` uses this; validator (E0913) gates which
    /// attribute names accept it.</summary>
    string? TypeArg,
    /// <summary>Named arguments (e.g. for [Skip(reason: "x", platform: "ios", feature: "jit")]).
    /// Null when the attribute has no parens or empty parens.</summary>
    IReadOnlyDictionary<string, string>? NamedArgs,
    Span Span);

// ── Type expressions ──────────────────────────────────────────────────────────

public abstract record TypeExpr(Span Span);
public sealed record NamedType(string Name, Span Span)   : TypeExpr(Span);
public sealed record OptionType(TypeExpr Inner, Span Span) : TypeExpr(Span);   // T?
public sealed record VoidType(Span Span)                 : TypeExpr(Span);
public sealed record ArrayType(TypeExpr Element, Span Span) : TypeExpr(Span);  // T[]
public sealed record GenericType(string Name, List<TypeExpr> TypeArgs, Span Span) : TypeExpr(Span);  // Box<int>, Dict<K,V>
public sealed record MemberType(TypeExpr Left, string Right, Span Span) : TypeExpr(Span);  // Outer.Inner (D-6: nested delegate dotted-path)

/// Function type `(T1, T2) -> R`. Equivalent to C# `Func<T1, T2, R>` /
/// `Action<T1, T2>` (resolved as same `Z42FuncType` in the semantic layer).
/// See docs/design/closure.md §3.2.
public sealed record FuncType(List<TypeExpr> ParamTypes, TypeExpr ReturnType, Span Span) : TypeExpr(Span);

// ── Statements ────────────────────────────────────────────────────────────────

public abstract record Stmt(Span Span);

/// Placeholder for a statement that failed to parse (error recovery).
public sealed record ErrorStmt(string Message, Span Span) : Stmt(Span);

/// var name = init;  or  Type name = init;
public sealed record VarDeclStmt(
    string Name,
    TypeExpr? TypeAnnotation,
    Expr? Init,
    Span Span) : Stmt(Span);

public sealed record ReturnStmt(Expr? Value, Span Span)  : Stmt(Span);
public sealed record ExprStmt(Expr Expr, Span Span)      : Stmt(Span);
public sealed record BlockStmt(List<Stmt> Stmts, Span Span) : Stmt(Span);

public sealed record IfStmt(
    Expr Condition, BlockStmt Then, Stmt? Else, Span Span) : Stmt(Span);

public sealed record WhileStmt(Expr Condition, BlockStmt Body, Span Span) : Stmt(Span);
public sealed record DoWhileStmt(BlockStmt Body, Expr Condition, Span Span) : Stmt(Span);

public sealed record ForStmt(
    Stmt? Init, Expr? Condition, Expr? Increment,
    BlockStmt Body, Span Span) : Stmt(Span);

public sealed record ForeachStmt(
    string VarName, Expr Collection, BlockStmt Body, Span Span) : Stmt(Span);

public sealed record BreakStmt(Span Span)    : Stmt(Span);
public sealed record ContinueStmt(Span Span) : Stmt(Span);

public sealed record SwitchStmt(
    Expr Subject, List<SwitchCase> Cases, Span Span) : Stmt(Span);

public sealed record SwitchCase(
    Expr? Pattern,   // null = default
    List<Stmt> Body,
    Span Span);

public sealed record TryCatchStmt(
    BlockStmt TryBody,
    List<CatchClause> Catches,
    BlockStmt? Finally,
    Span Span) : Stmt(Span);

public sealed record CatchClause(
    string? ExceptionType,
    string? VarName,
    BlockStmt Body,
    Span Span);

public sealed record ThrowStmt(Expr Value, Span Span) : Stmt(Span);

/// Local (nested) function declaration, e.g.
/// `int Outer() { int Helper(int x) => x * 2; return Helper(3); }`.
/// L2 阶段不允许捕获外层 local 变量（捕获是 L3 闭包特性）。
/// 见 docs/design/closure.md §3.4。
public sealed record LocalFunctionStmt(FunctionDecl Decl, Span Span) : Stmt(Span);

/// `pinned <Name> = <Source> { <Body> }` — borrows a `string` (and, in a
/// follow-up spec, `byte[]`) buffer for the duration of `Body` so it can
/// be passed to native code as a raw pointer + length pair. See spec C5
/// `impl-pinned-syntax` for typing rules and control-flow restrictions.
public sealed record PinnedStmt(
    string Name,
    Expr Source,
    BlockStmt Body,
    Span Span) : Stmt(Span);

// ── Expressions ───────────────────────────────────────────────────────────────

public abstract record Expr(Span Span);

/// Placeholder for an expression that failed to parse (error recovery).
public sealed record ErrorExpr(string Message, Span Span) : Expr(Span);

public sealed record LitIntExpr(long Value, Span Span)         : Expr(Span);
public sealed record LitFloatExpr(double Value, bool IsFloat, Span Span) : Expr(Span);
public sealed record LitStrExpr(string Value, Span Span)       : Expr(Span);
public sealed record LitBoolExpr(bool Value, Span Span)        : Expr(Span);
public sealed record LitNullExpr(Span Span)                    : Expr(Span);
public sealed record LitCharExpr(char Value, Span Span)        : Expr(Span);

/// $"text {expr} text"
public sealed record InterpolatedStrExpr(
    List<InterpolationPart> Parts, Span Span) : Expr(Span);

public abstract record InterpolationPart(Span Span);
public sealed record TextPart(string Text, Span Span)           : InterpolationPart(Span);
public sealed record ExprPart(Expr Inner, Span Span)            : InterpolationPart(Span);

public sealed record IdentExpr(string Name, Span Span)         : Expr(Span);
public sealed record BinaryExpr(string Op, Expr Left, Expr Right, Span Span) : Expr(Span);
public sealed record UnaryExpr(string Op, Expr Operand, Span Span) : Expr(Span);
public sealed record PostfixExpr(string Op, Expr Operand, Span Span) : Expr(Span);
public sealed record AssignExpr(Expr Target, Expr Value, Span Span) : Expr(Span);
public sealed record CallExpr(Expr Callee, List<Expr> Args, Span Span) : Expr(Span);
public sealed record MemberExpr(Expr Target, string Member, Span Span) : Expr(Span);
public sealed record IndexExpr(Expr Target, Expr Index, Span Span) : Expr(Span);
public sealed record ConditionalExpr(Expr Cond, Expr Then, Expr Else, Span Span) : Expr(Span);
/// `left ?? right` — returns left if non-null, otherwise right
public sealed record NullCoalesceExpr(Expr Left, Expr Right, Span Span) : Expr(Span);
public sealed record CastExpr(TypeExpr TargetType, Expr Operand, Span Span) : Expr(Span);
public sealed record NewExpr(TypeExpr Type, List<Expr> Args, Span Span) : Expr(Span);
/// new T[n]  — zero-initialized array of size n
public sealed record ArrayCreateExpr(TypeExpr ElemType, Expr Size, Span Span)           : Expr(Span);
/// new T[] { e0, e1, ... }  — array from literal elements
public sealed record ArrayLitExpr(TypeExpr ElemType, List<Expr> Elements, Span Span)    : Expr(Span);
/// Lambda parameter: `name` (untyped) or `Type name` (typed).
/// `Type == null` means the type is inferred from context (expected `Z42FuncType`).
public sealed record LambdaParam(string Name, TypeExpr? Type, Span Span);

/// Lambda body: either an expression (`x => x + 1`) or a block (`x => { return x; }`).
public abstract record LambdaBody(Span Span);
public sealed record LambdaExprBody(Expr Expr, Span Span)        : LambdaBody(Span);
public sealed record LambdaBlockBody(BlockStmt Block, Span Span) : LambdaBody(Span);

/// Lambda literal `params => body`. See docs/design/closure.md §3.1.
public sealed record LambdaExpr(List<LambdaParam> Params, LambdaBody Body, Span Span) : Expr(Span);
public sealed record SwitchExpr(Expr Subject, List<SwitchArm> Arms, Span Span) : Expr(Span);
public sealed record SwitchArm(Expr? Pattern, Expr Body, Span Span);

/// `target?.member` — returns null if target is null, otherwise accesses the member.
public sealed record NullConditionalExpr(Expr Target, string Member, Span Span) : Expr(Span);

/// `expr is TypeName binding` — type test with variable introduction.
/// The Binding variable is introduced into the containing scope (available in the then-branch).
public sealed record IsPatternExpr(Expr Target, string TypeName, string Binding, Span Span) : Expr(Span);
