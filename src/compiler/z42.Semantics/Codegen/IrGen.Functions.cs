using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{

    // ── Per-function delegation ──────────────────────────────────────────────

    private IrFunction EmitMethod(string className, FunctionDecl method,
        Tier1NativeBinding? classNativeDefaults = null,
        IReadOnlyList<FieldDecl>? instanceFieldInits = null)
    {
        // L3-Impl2: QualifyClassName routes imported impl targets to source namespace.
        // For local classes (cu.Classes path) this returns the same as QualifyName.
        var qualClass = ((IEmitterContext)this).QualifyClassName(className);
        // Overload detection via SemanticModel method keys (already $N suffixed).
        string arityKey = $"{method.Name}${method.Params.Count}";
        bool overloaded = method.IsStatic
            ? _classRegistry.TryGetStaticMethods(qualClass, out var sSet) && sSet.Contains(arityKey)
            : _classRegistry.TryGetMethods(qualClass, out var mSet) && mSet.Contains(arityKey);
        string methodIrName = overloaded
            ? $"{qualClass}.{arityKey}"
            : $"{qualClass}.{method.Name}";

        // Spec C9 — stitch method-level Tier1Binding with class-level
        // defaults so the method can omit lib/type when the class supplies
        // them. After stitching, IR codegen sees a complete (Lib, Type,
        // Entry) triple or null (legacy [Native("__name")] path).
        var stitchedTier1 = StitchTier1(method.Tier1Binding, classNativeDefaults);
        if (method.IsExtern && (method.NativeIntrinsic != null || stitchedTier1 != null))
        {
            var stub = EmitNativeStub(
                methodIrName,
                method.Params.Count + (method.IsStatic ? 0 : 1),
                method.IsStatic ? 0 : 1,
                method.NativeIntrinsic,
                stitchedTier1,
                method.ReturnType is VoidType);
            return stub with { IsStatic = method.IsStatic };
        }

        var body = GetBoundBody(method);
        return new FunctionEmitter(this).EmitMethod(
            className, method, body, methodIrName, instanceFieldInits);
    }

    /// Walks the local ancestor chain top-down (root → self) and concatenates
    /// each class's instance field initializers. Used to build the synth ctor's
    /// field-init injection list when a class declines explicit ctor.
    ///
    /// Imported (zpkg) ancestors are skipped — their FieldDecl AST is not
    /// available in this CU; cross-package field-init inheritance is out of
    /// scope for this fix and would require either propagating BoundExpr
    /// through TSIG or moving init evaluation to the importing side. See
    /// fix-class-field-default-init design notes.
    private List<FieldDecl> CollectChainFieldInits(
        ClassDecl cls, IReadOnlyDictionary<string, ClassDecl> localClassByName)
    {
        var chain = new List<ClassDecl>();
        var current = cls;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current.Name))
        {
            chain.Add(current);
            if (current.BaseClass is null) break;
            if (!localClassByName.TryGetValue(current.BaseClass, out var parent))
                break; // imported / unknown — stop walking
            current = parent;
        }
        chain.Reverse(); // root → self
        var result = new List<FieldDecl>();
        foreach (var c in chain)
            result.AddRange(c.Fields.Where(f => !f.IsStatic && f.Initializer != null));
        return result;
    }

    /// 2026-05-02 fix-class-field-default-init: 合成无参隐式 ctor。
    /// 当 class 没有显式 ctor 但本类或任一本地祖先有实例字段 initializer 时调用。
    /// 合成的 IR 函数名 = `{qualifiedClass}.{cls.Name}`，与 ResolveCtorName
    /// 在"无显式 ctor"路径返回的 className 拼接后一致；body 仅含字段 init
    /// （由 EmitMethod 的字段 init 注入逻辑产生），不包含 implicit base call
    /// （与 z42 现有"只有 `: base(...)` 才生成 base call"行为一致）。
    private IrFunction EmitImplicitCtor(ClassDecl cls, IReadOnlyList<FieldDecl> instanceFieldInits)
    {
        var synthCtor = new FunctionDecl(
            Name:            cls.Name,
            Params:          new List<Param>(),
            ReturnType:      new VoidType(cls.Span),
            Body:            new BlockStmt(new List<Stmt>(), cls.Span),
            Visibility:      Visibility.Public,
            Modifiers:       FunctionModifiers.None,
            NativeIntrinsic: null,
            Span:            cls.Span,
            BaseCtorArgs:    null);
        var emptyBody = new BoundBlock(Array.Empty<BoundStmt>(), cls.Span);
        // 2026-05-07 add-class-arity-overloading: use IR short name (mangled
        // when collision) so synthesized ctor IR name doesn't collide with the
        // non-generic same-source-name sibling.
        var clsShortIr = ClassIrShortName(cls);
        var qualClass  = ((IEmitterContext)this).QualifyClassName(clsShortIr);
        var ctorIrName = $"{qualClass}.{cls.Name}";
        return new FunctionEmitter(this).EmitMethod(
            clsShortIr, synthCtor, emptyBody, ctorIrName, instanceFieldInits);
    }

    private IrFunction EmitFunction(FunctionDecl fn)
    {
        if (fn.IsExtern && (fn.NativeIntrinsic != null || fn.Tier1Binding != null))
            return EmitNativeStub(
                QualifyName(fn.Name),
                fn.Params.Count,
                0,
                fn.NativeIntrinsic,
                fn.Tier1Binding,
                fn.ReturnType is VoidType);

        var body = GetBoundBody(fn);
        return new FunctionEmitter(this).EmitFunction(fn, body);
    }

    private BoundBlock GetBoundBody(FunctionDecl fn)
    {
        if (!_semanticModel!.BoundBodies.TryGetValue(fn, out var body))
            throw new InvalidOperationException(
                $"No BoundBody found for `{fn.Name}`; was it excluded from type-checking?");
        return body;
    }
}
