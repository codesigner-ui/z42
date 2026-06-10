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
            ? ((IEmitterContext)this).QualifyClassName(cls.BaseClass)
            : (cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name))
                ? null : "Std.Object";
        // C3 add-attribute-reflection: carry user attributes (each backed by a
        // synthesized factory func) so the runtime can build them for reflection.
        var attrs = cls.Attributes
            ?.Where(a => a.FactoryFunc is not null)
            .Select(a => new IrAttributeRef(
                ((IEmitterContext)this).QualifyClassName(a.Name),
                QualifyName(a.FactoryFunc!)))
            .ToList();
        if (attrs is { Count: 0 }) attrs = null;

        return new(QualifyName(shortName), baseClass,
            cls.Fields.Where(f => !f.IsStatic).Select(EmitFieldDesc).ToList(),
            cls.TypeParams?.ToList(),
            BuildConstraintList(shortName, cls.TypeParams, _semanticModel?.ClassConstraints),
            attrs,
            // add-reflection-type-flags (zbc 1.12): class-shape modifiers.
            IsAbstract: cls.IsAbstract, IsSealed: cls.IsSealed,
            IsStruct: cls.IsStruct, IsRecord: cls.IsRecord,
            // add-reflection-static-fields (zbc 1.13): static fields, separate
            // from the instance `Fields` list above.
            StaticFields: cls.Fields.Where(f => f.IsStatic).Select(EmitFieldDesc).ToList());
    }

    /// add-field-attribute-reflection (zbc 1.14): build an IrFieldDesc carrying
    /// the field's user-attribute refs (each → its synthesized factory func).
    private IrFieldDesc EmitFieldDesc(FieldDecl f)
    {
        var fattrs = f.Attributes
            ?.Where(a => a.FactoryFunc is not null)
            .Select(a => new IrAttributeRef(
                ((IEmitterContext)this).QualifyClassName(a.Name),
                QualifyName(a.FactoryFunc!)))
            .ToList();
        if (fattrs is { Count: 0 }) fattrs = null;
        return new IrFieldDesc(f.Name, TypeName(f.Type), fattrs);
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
                    b.RequiresEnum,
                    // add-generic-func-constraint (2026-05-11)
                    b.FuncSignature is { } sig
                        ? new IrFuncSig(sig.Params.Select(p => p.ToString()!).ToList(), sig.Ret.ToString()!)
                        : null));
            else
                result.Add(EmptyBundle());
        }
        return result;

        static IrConstraintBundle EmptyBundle() => new(false, false, null, new List<string>());
    }

    /// Emits a single-block stub function that forwards all parameters to a
    /// VM-side native dispatch. Three forms (exactly one of `intrinsicName`
    /// / `tier1` must be non-null):
    ///
    ///   * legacy `[Native("__name")]` (L1 stdlib)        → `BuiltinInstr`
    ///   * spec C6 `[Native(lib=, type=, entry=)]` (Tier 1) → `CallNativeInstr`
    ///   * add-z42-compression `[Native(lib=, entry=)]` (Tier 1 short form for
    ///     stdlib-internal native extensions, type= omitted)             → `BuiltinInstr`
    ///
    /// The Tier 1 short form bypasses libffi + Z42Value marshal (which
    /// requires spec C5 byte[] support, not yet done). The `entry` name
    /// is resolved at module-load time through the per-VM `ext_builtins`
    /// registry populated by `native::ext::load_all` from dlopened
    /// `lib<libname>.{so,dylib,dll}` artefacts.
    ///
    /// In all three cases the stub function itself has the same shape:
    /// arguments in r0..rN-1, result in rN, single block returning the
    /// result.
    ///
    /// `tier1` is the *stitched* binding (method + class defaults already
    /// merged via [`StitchTier1`]). When `tier1.TypeName` is null it's
    /// the short form; type-check guarantees `Lib` and `Entry` are then
    /// non-null. When all three Tier1 fields are present it's Tier 1
    /// proper (CallNativeInstr).
    private static IrFunction EmitNativeStub(
        string qualifiedName, int totalParams, int paramOffset,
        string? intrinsicName, Tier1NativeBinding? tier1, bool isVoid)
    {
        var args = Enumerable.Range(0, totalParams)
            .Select(i => new TypedReg(i, IrType.Unknown)).ToList();
        var dst  = new TypedReg(totalParams, isVoid ? IrType.Void : IrType.Unknown);
        IrInstr call;
        if (tier1 is { } t)
        {
            if (t.TypeName is null)
            {
                // Stdlib-internal short form: route through Builtin name lookup
                // (resolves via static BUILTINS[] or the VM-side ext_builtins
                // registry populated from dlopened lib<t.Lib>.{so,dylib,dll}).
                call = new BuiltinInstr(dst, t.Entry!, args);
            }
            else
            {
                call = new CallNativeInstr(dst, t.Lib!, t.TypeName, t.Entry!, args);
            }
        }
        else
        {
            call = new BuiltinInstr(dst, intrinsicName!, args);
        }
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
