using System.Runtime.CompilerServices;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// The output of the TypeChecker phase: a snapshot of all semantic information
/// derived from a single CompilationUnit.
///
/// Produced by <see cref="TypeChecker.Check"/> and consumed by IrGen so that
/// the code generator does not need to re-derive type information from the AST.
///
/// ExprTypes uses reference equality (ReferenceEqualityComparer) so that the
/// same AST node object maps to the type inferred during type-checking, even
/// when two structurally equal expressions appear at different call sites.
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

    /// Expression → inferred Z42Type mapping.
    /// Keyed by AST node reference identity (not structural equality).
    /// Populated for every expression visited by the TypeChecker.
    public IReadOnlyDictionary<Expr, Z42Type> ExprTypes { get; }

    internal SemanticModel(
        Dictionary<string, Z42ClassType>     classes,
        Dictionary<string, Z42FuncType>      funcs,
        Dictionary<string, Z42InterfaceType> interfaces,
        Dictionary<string, long>             enumConstants,
        HashSet<string>                      enumTypes,
        Dictionary<Expr, Z42Type>            exprTypes)
    {
        Classes       = classes;
        Funcs         = funcs;
        Interfaces    = interfaces;
        EnumConstants = enumConstants;
        EnumTypes     = enumTypes;
        ExprTypes     = exprTypes;
    }
}
