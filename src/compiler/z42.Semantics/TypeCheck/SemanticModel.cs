using Z42.Semantics.Bound;
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

    /// Bound base-constructor argument lists, keyed by the constructor FunctionDecl.
    /// Only populated for constructors that have a base-ctor call (FunctionDecl.BaseCtorArgs != null).
    public IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> BoundBaseCtorArgs { get; }

    internal SemanticModel(
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42FuncType>      funcs,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces,
        IReadOnlyDictionary<string, long>             enumConstants,
        IReadOnlySet<string>                          enumTypes,
        IReadOnlyDictionary<FunctionDecl, BoundBlock> boundBodies,
        IReadOnlyDictionary<Param,        BoundExpr>  boundDefaults,
        IReadOnlyDictionary<FieldDecl,    BoundExpr>  boundStaticInits,
        IReadOnlyDictionary<FunctionDecl, IReadOnlyList<BoundExpr>> boundBaseCtorArgs)
    {
        Classes           = classes;
        Funcs             = funcs;
        Interfaces        = interfaces;
        EnumConstants     = enumConstants;
        EnumTypes         = enumTypes;
        BoundBodies       = boundBodies;
        BoundDefaults     = boundDefaults;
        BoundStaticInits  = boundStaticInits;
        BoundBaseCtorArgs = boundBaseCtorArgs;
    }
}
