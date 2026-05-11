using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public sealed partial class TypeChecker
{

    // ── Call helpers ──────────────────────────────────────────────────────────

    private List<BoundExpr> BindAndCheckArgs(
        IReadOnlyList<Argument> args, TypeEnv env,
        IReadOnlyList<Z42Type> paramTypes, int minArgCount, Span callSpan)
    {
        var bound = args.Select(a => BindArgValue(a, env)).ToList();
        CheckArgCount(bound.Count, minArgCount, paramTypes.Count, callSpan);
        CheckArgTypes(args, bound, paramTypes);
        return bound;
    }

    /// L3-G4h step3: List/Dictionary 已迁移到源码类；2026-04-26 script-first-stringbuilder
    /// 把 StringBuilder 也迁到源码类。仅 Array 保留 pseudo 路径
    /// (Array 是 VM 原生 Value variant，不是 z42 class)。
    private bool IsBuiltinCollectionType(string typeName)
    {
        return typeName == "Array";
    }

    private void CheckArgCount(int actual, int min, int max, Span span)
    {
        if (actual < min || actual > max)
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"expected {min}–{max} argument(s), got {actual}", span);
    }

}
