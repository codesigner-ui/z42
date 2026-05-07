using Z42.Core;
using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public static partial class ImportedSymbolLoader
{

    // ── Phase 1 helpers: 骨架构造 ──────────────────────────────────────────────

    private static Z42ClassType BuildClassSkeleton(ExportedClassDef cls, string importKey)
    {
        IReadOnlyList<string>? typeParams = cls.TypeParams is { Count: > 0 }
            ? cls.TypeParams.AsReadOnly() : null;
        // 2026-05-07 add-class-arity-overloading: when the import key is
        // the mangled `Name$N` form, mark the class so its IrName matches
        // (consumer codegen / IR encoding picks up the mangled identity).
        bool hasArityMangle = importKey != cls.Name;
        return new Z42ClassType(
            cls.Name,
            Fields:           new Dictionary<string, Z42Type>(),
            Methods:          new Dictionary<string, Z42FuncType>(),
            StaticFields:     new Dictionary<string, Z42Type>(),
            StaticMethods:    new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName:    cls.BaseClass,
            TypeParams:       typeParams,
            IsStruct:         false,
            HasArityMangle:   hasArityMangle);
    }

    private static Z42InterfaceType BuildInterfaceSkeleton(ExportedInterfaceDef iface)
    {
        IReadOnlyList<string>? typeParams = iface.TypeParams is { Count: > 0 } tps
            ? tps.AsReadOnly() : null;
        return new Z42InterfaceType(
            iface.Name,
            Methods:       new Dictionary<string, Z42FuncType>(),
            TypeArgs:      null,
            StaticMembers: null,
            TypeParams:    typeParams);
    }
}
