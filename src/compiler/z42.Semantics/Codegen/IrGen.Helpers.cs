using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{

    // ── Module-level helpers ─────────────────────────────────────────────────

    internal string QualifyName(string name) =>
        _namespace is null ? name : $"{_namespace}.{name}";

    string? IEmitterContext.CompilationUnitStem => _cuStem;

    internal int Intern(string s)
    {
        if (_stringIndex.TryGetValue(s, out int idx)) return idx;
        idx = _strings.Count;
        _strings.Add(s);
        _stringIndex[s] = idx;
        return idx;
    }

    /// Returns instance field names for a class, including all inherited fields.
    internal HashSet<string> GetClassInstanceFieldNames(string className) =>
        _classRegistry.GetAllInstanceFields(QualifyName(className));

    /// Finds the _funcParams key for a virtual call default expansion.
    internal string? FindVcallParamsKey(string methodName, int suppliedArgCount) =>
        _classRegistry.FindVcallParamsKey(methodName, suppliedArgCount, _funcParams);

    /// Returns the qualified static field key if className has a static field named fieldName.
    ///
    /// 2026-04-27 fix-static-field-access：使用 `QualifyClassName` 而非 `QualifyName`，
    /// 这样 imported class（如 `Math` from `z42.math`）的字段会拿到正确的 import
    /// namespace（`Std.Math.Math.PI`），与 zpkg 内 `__static_init__` 写入的 key
    /// 一致；否则用户代码 emit `@Math.PI`，VM HashMap 找不到 → 返回 null。
    internal string? TryGetStaticFieldKey(string className, string fieldName)
    {
        if (_semanticModel!.Classes.TryGetValue(className, out var ct)
            && ct.StaticFields.ContainsKey(fieldName))
            return $"{((IEmitterContext)this).QualifyClassName(className)}.{fieldName}";
        return null;
    }

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        // 2026-04-28 fix-generic-type-roundtrip：保留 generic type-args
        GenericType gt => $"{gt.Name}<{string.Join(", ", gt.TypeArgs.Select(TypeName))}>",
        _             => "unknown"
    };
}
