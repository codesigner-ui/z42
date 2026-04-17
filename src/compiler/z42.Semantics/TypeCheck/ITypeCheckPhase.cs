using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Pass 0: collects type shapes from a CompilationUnit into an immutable SymbolTable.
/// </summary>
public interface ISymbolBinder
{
    SymbolTable Collect(CompilationUnit cu, ImportedSymbols? imported = null);
}

/// <summary>
/// Pass 1: binds function/method bodies using a frozen SymbolTable.
/// Produces bound trees (BoundBlock per function) and default-value bindings.
/// </summary>
public interface ITypeInferrer
{
    SemanticModel Infer(CompilationUnit cu, SymbolTable symbols);
}

/// <summary>
/// Pass 2: post-binding analysis on bound trees (reachability, definite assignment).
/// Stateless — operates on individual BoundBlocks.
/// </summary>
public interface IFlowAnalyzer
{
    bool AlwaysReturns(BoundBlock block);
    void CheckDefiniteAssignment(BoundBlock block, DiagnosticBag diags);
}
