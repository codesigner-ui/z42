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
    Span Span);

// ── Generic constraints (L3-G2) ────────────────────────────────────────────────

/// `where T: IFoo + IBar`
///
/// `TypeParam` names the constrained type parameter (must match one declared in
/// the enclosing TypeParams list); `Constraints` lists the interfaces/types it
/// must implement (multiple combined with `+`).
public sealed record GenericConstraint(
    string TypeParam,
    List<TypeExpr> Constraints,
    Span Span);

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

/// A method signature (no body) for use in interface declarations.
public sealed record MethodSignature(
    string Name,
    List<Param> Params,
    TypeExpr ReturnType,
    Span Span);

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
    string? BaseClass,          // null = no explicit base class
    List<string> Interfaces,    // list of implemented interface names
    List<FieldDecl> Fields,
    List<FunctionDecl> Methods,
    Span Span,
    List<string>? TypeParams = null,
    WhereClause? Where = null);  // L3-G2 generic constraints

/// A field inside a class/struct: `int x;`
public sealed record FieldDecl(
    string Name,
    TypeExpr Type,
    Visibility Visibility,
    bool IsStatic,
    Expr? Initializer,
    Span Span);

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
    WhereClause? Where = null)        // L3-G2 generic constraints
{
    // Convenience accessors — keep read sites concise
    public bool IsStatic   => Modifiers.HasFlag(FunctionModifiers.Static);
    public bool IsVirtual  => Modifiers.HasFlag(FunctionModifiers.Virtual);
    public bool IsOverride => Modifiers.HasFlag(FunctionModifiers.Override);
    public bool IsAbstract => Modifiers.HasFlag(FunctionModifiers.Abstract);
    public bool IsExtern   => Modifiers.HasFlag(FunctionModifiers.Extern);
}

public sealed record Param(string Name, TypeExpr Type, Expr? Default, Span Span);

// ── Type expressions ──────────────────────────────────────────────────────────

public abstract record TypeExpr(Span Span);
public sealed record NamedType(string Name, Span Span)   : TypeExpr(Span);
public sealed record OptionType(TypeExpr Inner, Span Span) : TypeExpr(Span);   // T?
public sealed record VoidType(Span Span)                 : TypeExpr(Span);
public sealed record ArrayType(TypeExpr Element, Span Span) : TypeExpr(Span);  // T[]
public sealed record GenericType(string Name, List<TypeExpr> TypeArgs, Span Span) : TypeExpr(Span);  // Box<int>, Dict<K,V>

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
public sealed record LambdaExpr(List<string> Params, Expr Body, Span Span) : Expr(Span);
public sealed record SwitchExpr(Expr Subject, List<SwitchArm> Arms, Span Span) : Expr(Span);
public sealed record SwitchArm(Expr? Pattern, Expr Body, Span Span);

/// `target?.member` — returns null if target is null, otherwise accesses the member.
public sealed record NullConditionalExpr(Expr Target, string Member, Span Span) : Expr(Span);

/// `expr is TypeName binding` — type test with variable introduction.
/// The Binding variable is introduced into the containing scope (available in the then-branch).
public sealed record IsPatternExpr(Expr Target, string TypeName, string Binding, Span Span) : Expr(Span);
