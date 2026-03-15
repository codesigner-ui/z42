using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

// ── Root ──────────────────────────────────────────────────────────────────────

public sealed record Module(string Name, List<Item> Items, Span Span);

// ── Top-level items ───────────────────────────────────────────────────────────

public abstract record Item(Span Span);

public sealed record FunctionItem(
    string Name,
    List<Param> Params,
    TypeExpr ReturnType,
    BlockExpr Body,
    Span Span) : Item(Span);

public sealed record StructItem(
    string Name,
    List<FieldDef> Fields,
    Span Span) : Item(Span);

public sealed record EnumItem(
    string Name,
    List<VariantDef> Variants,
    Span Span) : Item(Span);

public sealed record TraitItem(
    string Name,
    List<FunctionItem> Methods,
    Span Span) : Item(Span);

public sealed record ImplItem(
    TypeExpr Target,
    string? TraitName,
    List<FunctionItem> Methods,
    Span Span) : Item(Span);

public sealed record UseItem(string Path, Span Span) : Item(Span);

// ── Declarations ──────────────────────────────────────────────────────────────

public sealed record Param(string Name, TypeExpr Type, Span Span);
public sealed record FieldDef(string Name, TypeExpr Type, Span Span);
public sealed record VariantDef(string Name, List<TypeExpr> Payload, Span Span);

// ── Type expressions ──────────────────────────────────────────────────────────

public abstract record TypeExpr(Span Span);
public sealed record NamedType(string Name, Span Span) : TypeExpr(Span);
public sealed record OptionType(TypeExpr Inner, Span Span) : TypeExpr(Span);         // T?
public sealed record ResultType(TypeExpr Inner, Span Span) : TypeExpr(Span);         // T!
public sealed record RefType(TypeExpr Inner, bool IsMut, Span Span) : TypeExpr(Span); // &T / &mut T
public sealed record VoidType(Span Span) : TypeExpr(Span);

// ── Statements ────────────────────────────────────────────────────────────────

public abstract record Stmt(Span Span);
public sealed record LetStmt(string Name, bool IsMut, TypeExpr? TypeAnnotation, Expr? Init, Span Span) : Stmt(Span);
public sealed record ReturnStmt(Expr? Value, Span Span) : Stmt(Span);
public sealed record ExprStmt(Expr Expr, Span Span) : Stmt(Span);

// ── Expressions ───────────────────────────────────────────────────────────────

public abstract record Expr(Span Span);

public sealed record BlockExpr(List<Stmt> Stmts, Expr? Tail, Span Span) : Expr(Span);
public sealed record LitIntExpr(long Value, Span Span) : Expr(Span);
public sealed record LitFloatExpr(double Value, Span Span) : Expr(Span);
public sealed record LitStrExpr(string Value, Span Span) : Expr(Span);
public sealed record LitBoolExpr(bool Value, Span Span) : Expr(Span);
public sealed record NoneExpr(Span Span) : Expr(Span);
public sealed record IdentExpr(string Name, Span Span) : Expr(Span);
public sealed record BinaryExpr(string Op, Expr Left, Expr Right, Span Span) : Expr(Span);
public sealed record UnaryExpr(string Op, Expr Operand, Span Span) : Expr(Span);
public sealed record CallExpr(Expr Callee, List<Expr> Args, Span Span) : Expr(Span);
public sealed record FieldExpr(Expr Target, string Field, Span Span) : Expr(Span);
public sealed record IfExpr(Expr Condition, BlockExpr Then, Expr? Else_, Span Span) : Expr(Span);
public sealed record MatchExpr(Expr Subject, List<MatchArm> Arms, Span Span) : Expr(Span);
public sealed record MatchArm(Pattern Pattern, Expr Body, Span Span);
public sealed record AwaitExpr(Expr Inner, Span Span) : Expr(Span);

// ── Patterns ──────────────────────────────────────────────────────────────────

public abstract record Pattern(Span Span);
public sealed record WildcardPattern(Span Span) : Pattern(Span);
public sealed record IdentPattern(string Name, Span Span) : Pattern(Span);
public sealed record VariantPattern(string Variant, List<Pattern> Fields, Span Span) : Pattern(Span);
public sealed record LitPattern(Expr Literal, Span Span) : Pattern(Span);
