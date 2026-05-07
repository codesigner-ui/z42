using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{

    // ── Class descriptors ────────────────────────────────────────────────────

    /// 2026-05-07 add-class-arity-overloading: returns the IR-side class name —
    /// arity-suffixed (`Foo$N`) when this class has a same-name non-generic
    /// sibling (HasArityMangle=true); bare `cls.Name` otherwise. Used for IR
    /// class declaration / FQ name emission so collision pairs survive into
    /// distinct VM type_registry entries.
    private string ClassIrShortName(ClassDecl cls)
    {
        int arity = cls.TypeParams?.Count ?? 0;
        if (arity > 0
            && _semanticModel!.Classes.TryGetValue($"{cls.Name}${arity}", out var manglee)
            && manglee.HasArityMangle)
            return manglee.IrName;
        return cls.Name;
    }

    private IrClassDesc EmitClassDesc(ClassDecl cls)
    {
        var shortName = ClassIrShortName(cls);
        var baseClass = cls.BaseClass is not null
            ? QualifyName(cls.BaseClass)
            : (cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name))
                ? null : "Std.Object";
        return new(QualifyName(shortName), baseClass,
            cls.Fields.Where(f => !f.IsStatic)
                .Select(f => new IrFieldDesc(f.Name, TypeName(f.Type))).ToList(),
            cls.TypeParams?.ToList(),
            BuildConstraintList(shortName, cls.TypeParams, _semanticModel?.ClassConstraints));
    }

    /// (L3-G3a) Build a parallel list of IrConstraintBundle aligned with `typeParams`.
    /// Returns null when the decl has no type params; returns a list with one entry per type
    /// param (empty bundle for unconstrained ones) otherwise.
    internal static List<IrConstraintBundle>? BuildConstraintList(
        string declName,
        IReadOnlyList<string>? typeParams,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>? map)
    {
        if (typeParams is null || typeParams.Count == 0) return null;
        if (map is null || !map.TryGetValue(declName, out var bundles))
        {
            // Emit explicit empty bundles so reader/VM get aligned slots.
            return typeParams.Select(_ => EmptyBundle()).ToList();
        }
        var result = new List<IrConstraintBundle>(typeParams.Count);
        foreach (var tp in typeParams)
        {
            if (bundles.TryGetValue(tp, out var b))
                result.Add(new IrConstraintBundle(
                    b.RequiresClass, b.RequiresStruct,
                    b.BaseClass?.Name, b.Interfaces.Select(i => i.Name).ToList(),
                    b.TypeParamConstraint,
                    b.RequiresConstructor,
                    b.RequiresEnum));
            else
                result.Add(EmptyBundle());
        }
        return result;

        static IrConstraintBundle EmptyBundle() => new(false, false, null, new List<string>());
    }

    /// Emits a single-block stub function that forwards all parameters to a
    /// VM-side native dispatch. Two forms (mutually exclusive — exactly one
    /// of `intrinsicName` / `tier1` must be non-null):
    ///
    ///   * legacy `[Native("__name")]` (L1 stdlib) → `BuiltinInstr`
    ///   * spec C6 `[Native(lib=, type=, entry=)]`  → `CallNativeInstr`
    ///
    /// In both cases the stub function itself has the same shape: arguments
    /// in r0..rN-1, result in rN, single block returning the result.
    ///
    /// `tier1` is the *stitched* binding (method + class defaults already
    /// merged via [`StitchTier1`]). All three fields must be non-null when
    /// non-null is passed; type-check guarantees this otherwise emits E0907.
    private static IrFunction EmitNativeStub(
        string qualifiedName, int totalParams, int paramOffset,
        string? intrinsicName, Tier1NativeBinding? tier1, bool isVoid)
    {
        var args = Enumerable.Range(0, totalParams)
            .Select(i => new TypedReg(i, IrType.Unknown)).ToList();
        var dst  = new TypedReg(totalParams, isVoid ? IrType.Void : IrType.Unknown);
        IrInstr call = tier1 is { } t
            ? new CallNativeInstr(dst, t.Lib!, t.TypeName!, t.Entry!, args)
            : new BuiltinInstr(dst, intrinsicName!, args);
        var instrs = new List<IrInstr> { call };
        var term   = new RetTerm(isVoid ? null : dst);
        var block  = new IrBlock("entry", instrs, term);
        return new IrFunction(qualifiedName, totalParams, isVoid ? "void" : "object",
            "Interp", [block], null, MaxReg: totalParams + 1);
    }

    /// Spec C9 — combine a method's `[Native(...)]` binding with its enclosing
    /// class's defaults. Method fields override class fields; missing fields
    /// from one source are filled by the other. Returns null if neither side
    /// supplies any Tier1 info (legacy path); returns a binding with possibly
    /// null fields otherwise — caller must validate completeness via
    /// type-check (which raises E0907 on null fields).
    internal static Tier1NativeBinding? StitchTier1(
        Tier1NativeBinding? methodBinding,
        Tier1NativeBinding? classDefaults)
    {
        if (methodBinding is null && classDefaults is null) return null;
        return new Tier1NativeBinding(
            Lib:      methodBinding?.Lib      ?? classDefaults?.Lib,
            TypeName: methodBinding?.TypeName ?? classDefaults?.TypeName,
            Entry:    methodBinding?.Entry    ?? classDefaults?.Entry);
    }
}
