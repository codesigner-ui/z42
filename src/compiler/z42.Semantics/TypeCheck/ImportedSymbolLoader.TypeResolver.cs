using Z42.Core;
using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public static partial class ImportedSymbolLoader
{

    private static Z42FuncType RebuildFuncType(
        List<ExportedParamDef>                         parms,
        string                                         retType,
        int                                            minArgCount,
        HashSet<string>?                               genericParams = null,
        IReadOnlyDictionary<string, Z42ClassType>?     classes       = null,
        IReadOnlyDictionary<string, Z42InterfaceType>? interfaces    = null,
        IReadOnlyDictionary<string, DelegateInfo>?     delegates     = null)
    {
        var paramTypes = parms.Select(p =>
            ResolveTypeName(p.TypeName, genericParams, classes, interfaces, delegates)).ToList();
        return new Z42FuncType(paramTypes,
            ResolveTypeName(retType, genericParams, classes, interfaces, delegates),
            minArgCount == paramTypes.Count ? -1 : minArgCount);
    }

    /// Resolve a type name string (as serialized in TSIG) back to a Z42Type.
    ///
    /// Lookup precedence:
    ///   1. Suffixed forms: `T[]` → array, `T?` → optional
    ///   2. Generic param in scope (`T` etc.)
    ///   3. Built-in primitive (`int`, `string`, ...)
    ///   4. Imported class type (from `classes` dict — Phase 2 only)
    ///   5. Imported interface type (from `interfaces` dict — Phase 2 only)
    ///   6. Fallback: `Z42PrimType(name)` —— Phase 1 占位 / 真未知
    ///
    /// Phase 1 调用时 classes / interfaces 为 null（仅基本元素 + sentinel），
    /// Phase 2 调用必须传入完整字典以避免降级。
    internal static Z42Type ResolveTypeName(
        string                                         name,
        HashSet<string>?                               genericParams = null,
        IReadOnlyDictionary<string, Z42ClassType>?     classes       = null,
        IReadOnlyDictionary<string, Z42InterfaceType>? interfaces    = null,
        IReadOnlyDictionary<string, DelegateInfo>?     delegates     = null)
    {
        if (name.EndsWith("[]"))
            return new Z42ArrayType(ResolveTypeName(name[..^2], genericParams, classes, interfaces, delegates));
        if (name.EndsWith("?"))
            return new Z42OptionType(ResolveTypeName(name[..^1], genericParams, classes, interfaces, delegates));
        // 2026-04-28 fix-generic-type-roundtrip：识别 `Foo<X, Y, ...>` 字符串，
        // 重建为 `Z42InstantiatedType { Definition = Foo, TypeArgs = [X, Y, ...] }`。
        // 否则 `KeyValuePair<K, V>` 当 unknown class 处理 → 跨 zpkg 后退化为
        // PrimType("KeyValuePair<K, V>")，用户访问 `.Value` 拿到 generic V。
        if (name.EndsWith(">"))
        {
            int lt = FindGenericOpenLt(name);
            if (lt > 0)
            {
                string baseName = name[..lt];
                string argsStr  = name[(lt + 1)..^1];
                var resolvedArgs = SplitGenericArgs(argsStr)
                    .Select(s => ResolveTypeName(s.Trim(), genericParams, classes, interfaces, delegates))
                    .ToList();
                // 2026-05-02 D2a: generic delegate (`Action<T>` / `Func<T,R>`) 实例化
                if (delegates is not null
                    && delegates.TryGetValue($"{baseName}${resolvedArgs.Count}", out var di)
                    && di.TypeParams.Count == resolvedArgs.Count)
                {
                    var subMap = new Dictionary<string, Z42Type>(di.TypeParams.Count);
                    for (int i = 0; i < di.TypeParams.Count; i++)
                        subMap[di.TypeParams[i]] = resolvedArgs[i];
                    return TypeChecker.SubstituteTypeParams(di.Signature, subMap);
                }
                if (classes is not null && classes.TryGetValue(baseName, out var defCls))
                {
                    return new Z42InstantiatedType(defCls, resolvedArgs);
                }
            }
        }
        if (genericParams != null && genericParams.Contains(name))
            return new Z42GenericParamType(name);
        // 2026-05-02 D2a: 非泛型 delegate 名（如 `Action`，0-arity）解析为 Z42FuncType
        if (delegates is not null
            && delegates.TryGetValue(name, out var di0)
            && di0.TypeParams.Count == 0)
            return di0.Signature;

        switch (name)
        {
            case "int"    or "i32": return Z42Type.Int;
            case "long"   or "i64": return Z42Type.Long;
            case "float"  or "f32": return Z42Type.Float;
            case "double" or "f64": return Z42Type.Double;
            case "bool":            return Z42Type.Bool;
            case "string":          return Z42Type.String;
            case "char":            return Z42Type.Char;
            case "object":          return Z42Type.Object;
            case "void":            return Z42Type.Void;
            case "null":            return Z42Type.Null;
            case "i8":              return Z42Type.I8;
            case "i16":             return Z42Type.I16;
            case "u8":              return Z42Type.U8;
            case "u16":             return Z42Type.U16;
            case "u32":             return Z42Type.U32;
            case "u64":             return Z42Type.U64;
            case "unknown":         return Z42Type.Unknown;
            case "error":           return Z42Type.Error;
        }

        // Phase 2: lookup imported class / interface registries to avoid
        // PrimType downgrade for self-reference / forward-reference fields.
        if (classes is not null && classes.TryGetValue(name, out var ct))
            return ct;
        if (interfaces is not null && interfaces.TryGetValue(name, out var it))
            return it;

        // Fallback: Phase 1 占位 / 真正未知名（拼写错误等，TypeChecker 后续报错）
        return new Z42PrimType(name);
    }

    /// 找到 `Foo<X, Bar<Y, Z>>` 字符串里最外层 `<` 的 index。深度计数处理嵌套
    /// generic（按 `<` `>` 平衡 + 跟最右 `>` 配对）。无匹配返回 -1。
    private static int FindGenericOpenLt(string name)
    {
        if (name.Length == 0 || name[^1] != '>') return -1;
        int depth = 0;
        for (int i = name.Length - 1; i >= 0; i--)
        {
            if      (name[i] == '>') depth++;
            else if (name[i] == '<')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// 切分 `K, V`、`K, Foo<X, Y>` 等 generic-args 字符串，按顶层 `,` 分隔。
    /// 嵌套 `<>` 内的 `,` 不切。
    private static IEnumerable<string> SplitGenericArgs(string args)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    yield return args[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start <= args.Length) yield return args[start..];
    }

}
