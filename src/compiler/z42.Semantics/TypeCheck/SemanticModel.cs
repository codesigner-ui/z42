using Z42.Semantics.Bound;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// The output of the TypeChecker phase: a snapshot of all semantic information
/// derived from a single CompilationUnit.
///
/// Produced by <see cref="TypeChecker.Check"/> and consumed by IrGen so that
/// the code generator does not need to re-derive type information from the AST.
///
/// BoundBodies maps each FunctionDecl to its fully-bound BoundBlock, so that
/// FunctionEmitter receives typed nodes and needs no ExprTypes dictionary lookup.
/// BoundDefaults, BoundStaticInits, BoundBaseCtorArgs cover the remaining raw-AST
/// expressions, eliminating the legacy EmitRawExpr fallback entirely.
/// </summary>
public sealed class SemanticModel
{
    /// All user-defined classes in this compilation unit (unqualified short name → type).
    /// Includes inherited members merged by the second TypeChecker pass.
    /// Also includes the pre-registered "Object" stub when the CU does not define Object.
    public IReadOnlyDictionary<string, Z42ClassType> Classes { get; }

    /// Top-level function signatures (unqualified short name → type).
    public IReadOnlyDictionary<string, Z42FuncType> Funcs { get; }

    /// All interface types declared in this CU (short name → type).
    public IReadOnlyDictionary<string, Z42InterfaceType> Interfaces { get; }

    /// Fully-qualified enum constant values: "EnumName.Member" → i64 value.
    public IReadOnlyDictionary<string, long> EnumConstants { get; }

    /// Short names of declared enum types.
    public IReadOnlySet<string> EnumTypes { get; }

    /// Bound bodies for each non-extern FunctionDecl (class methods + top-level functions).
    /// Extern/native functions are absent; callers must check before accessing.
    public IReadOnlyDictionary<FunctionDecl, BoundBlock> BoundBodies { get; }

    /// Bound default-value expressions keyed by Param (reference equality).
    /// Only populated for params that have a default (Param.Default != null).
    public IReadOnlyDictionary<Param, BoundExpr> BoundDefaults { get; }

    /// Bound static field initializers keyed by FieldDecl (reference equality).
    /// Only populated for static fields that have an initializer (FieldDecl.Initializer != null).
    public IReadOnlyDictionary<FieldDecl, BoundExpr> BoundStaticInits { get; }

    /// Bound instance field initializers keyed by FieldDecl (reference equality).
    /// Only populated for non-static fields that have an initializer.
    /// Codegen 在每个 ctor body 头部（base ctor call 之后）按字段声明顺序发射
    /// `this.<field> = <init-expr>`；无显式 ctor 但任一字段有显式 init 时，
    /// IrGen 合成无参隐式 ctor 跑这些 init。
    public IReadOnlyDictionary<FieldDecl, BoundExpr> BoundInstanceInits { get; }

    /// Bound base-constructor argument lists, keyed by the constructor FunctionDecl.
    /// Only populated for constructors that have a base-ctor call (FunctionDecl.BaseCtorArgs != null).
    public IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> BoundBaseCtorArgs { get; }

    /// 2026-05-05 fix-ctor-delegation: bound `: this(args)` argument lists,
    /// keyed by the delegating ctor's FunctionDecl. Mutually exclusive with
    /// `BoundBaseCtorArgs` for the same key.
    public IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> BoundThisCtorArgs { get; }

    /// Maps imported class short name → dependency namespace (e.g. "Console" → "Std.IO").
    /// Used by IrGen to qualify imported class calls with the correct namespace.
    public IReadOnlyDictionary<string, string> ImportedClassNamespaces { get; }

    /// (L3-G4d) Short names of classes imported from dependencies (not locally defined).
    /// Consumed by `IrGen.QualifyClassName` so local classes win over same-named imports.
    public IReadOnlySet<string> ImportedClassNames { get; }

    /// Resolved generic constraints per top-level function name. (L3-G3a)
    /// Inner dict keys = type-param name; value = bundle. Absent entries → no constraints.
    /// Consumed by IrGen to emit zbc constraint metadata.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>
        FuncConstraints { get; }

    /// Resolved generic constraints per class name. (L3-G3a)
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>
        ClassConstraints { get; }

    /// L3-G4b primitive-as-struct: class short name → declared interface types.
    /// Exposed so ExportedTypeExtractor can write TSIG `ExportedClassDef.Interfaces`.
    public IReadOnlyDictionary<string, List<Z42InterfaceType>> ClassInterfaces { get; }

    /// 2026-05-02 impl-closure-l3-escape-stack: BoundLambda 集合，写入此集合的
    /// lambda 经 escape 分析证明 env 不离开创建 frame → Codegen 把
    /// `MkClosInstr.StackAlloc` 设为 true → VM 走 frame-local arena
    /// (`Value::StackClosure`)。reference-equality keyed（与 BoundDefaults 同款）。
    public IReadOnlySet<BoundLambda> StackAllocClosures { get; }

    /// 2026-05-02 add-delegate-type / add-generic-delegates: user-declared
    /// delegate type registry. Same shape as `SymbolTable.Delegates` —
    /// re-exposed here so `ExportedTypeExtractor` can serialize delegate
    /// signatures into TSIG.
    public IReadOnlyDictionary<string, DelegateInfo> Delegates { get; }

    /// review.md F2.3 Phase 1 (2026-06-03): every AST `Expr` that
    /// TypeChecker bound (via `BindExpr`) is mapped here to its resulting
    /// `BoundExpr`. Reference-equality keyed — the same literal value at
    /// two source positions gets two distinct entries. Backs
    /// <see cref="GetBoundExpression"/> + <see cref="GetExpressionType"/>.
    /// Empty for compilation units that hit only declaration-level bindings
    /// without expressions.
    public IReadOnlyDictionary<Expr, BoundExpr> ExpressionBindings { get; }

    /// Returns the typed bound form of the given AST expression, or `null`
    /// if the TypeChecker never bound it (e.g. node lives inside a
    /// skipped extern body, or was elided during error recovery).
    /// Reference-equality lookup — pass the same `Expr` node instance you
    /// had during AST construction; structurally-equal-but-distinct nodes
    /// won't match.
    public BoundExpr? GetBoundExpression(Expr astNode) =>
        ExpressionBindings.TryGetValue(astNode, out var bound) ? bound : null;

    /// Convenience: the inferred Z42 type of `astNode`'s bound form, or
    /// `null` if not bound. Equivalent to
    /// `GetBoundExpression(astNode)?.Type`.
    public Z42Type? GetExpressionType(Expr astNode) =>
        GetBoundExpression(astNode)?.Type;

    /// review.md F2.2 Phase 1 (2026-06-03, add-isymbol-base-phase1): the
    /// resolved <see cref="ISymbol"/> referenced by <paramref name="astNode"/>,
    /// or <c>null</c> if the node either isn't bound or doesn't reference
    /// a symbol that's been carrying its symbol pointer through
    /// TypeChecker.
    ///
    /// Phase 1 covers direct method-dispatch calls:
    ///
    /// <list type="bullet">
    ///   <item><see cref="CallExpr"/> → <see cref="BoundCall.Symbol"/>
    ///     (an <see cref="Symbols.IMethodSymbol"/>).</item>
    /// </list>
    ///
    /// Phase 2-3 will extend to <c>BoundIdent</c> (locals / params /
    /// fields), <c>BoundMember</c> (field / method group access), and
    /// declaration-site lookup via <c>GetDeclaredSymbol</c>.
    public ISymbol? GetSymbol(Expr astNode)
    {
        var bound = GetBoundExpression(astNode);
        return bound switch
        {
            BoundCall { Symbol: { } sym } => sym,
            _                              => null,
        };
    }

    internal SemanticModel(
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42FuncType>      funcs,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces,
        IReadOnlyDictionary<string, long>             enumConstants,
        IReadOnlySet<string>                          enumTypes,
        IReadOnlyDictionary<FunctionDecl, BoundBlock> boundBodies,
        IReadOnlyDictionary<Param,        BoundExpr>  boundDefaults,
        IReadOnlyDictionary<FieldDecl,    BoundExpr>  boundStaticInits,
        IReadOnlyDictionary<FieldDecl,    BoundExpr>  boundInstanceInits,
        IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> boundBaseCtorArgs,
        IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> boundThisCtorArgs,
        IReadOnlyDictionary<string, string>? importedClassNamespaces = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>? funcConstraints = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>? classConstraints = null,
        IReadOnlySet<string>? importedClassNames = null,
        IReadOnlyDictionary<string, List<Z42InterfaceType>>? classInterfaces = null,
        IReadOnlySet<BoundLambda>? stackAllocClosures = null,
        IReadOnlyDictionary<string, DelegateInfo>? delegates = null,
        IReadOnlyDictionary<Expr, BoundExpr>? exprBindings = null)
    {
        Classes                 = classes;
        Funcs                   = funcs;
        Interfaces              = interfaces;
        EnumConstants           = enumConstants;
        EnumTypes               = enumTypes;
        BoundBodies             = boundBodies;
        BoundDefaults           = boundDefaults;
        BoundStaticInits        = boundStaticInits;
        BoundInstanceInits      = boundInstanceInits;
        BoundBaseCtorArgs       = boundBaseCtorArgs;
        BoundThisCtorArgs       = boundThisCtorArgs;
        ImportedClassNamespaces = importedClassNamespaces ?? new Dictionary<string, string>();
        FuncConstraints         = funcConstraints  ?? new Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>();
        ClassConstraints        = classConstraints ?? new Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>();
        ImportedClassNames      = importedClassNames ?? new HashSet<string>();
        ClassInterfaces         = classInterfaces ?? new Dictionary<string, List<Z42InterfaceType>>();
        StackAllocClosures      = stackAllocClosures ?? new HashSet<BoundLambda>(ReferenceEqualityComparer.Instance);
        Delegates               = delegates ?? new Dictionary<string, DelegateInfo>();
        ExpressionBindings      = exprBindings ?? new Dictionary<Expr, BoundExpr>(ReferenceEqualityComparer.Instance);
    }
}
