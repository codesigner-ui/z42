using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Emits IR for a single function/method body.
/// Created fresh per function to naturally isolate function-level state,
/// eliminating the risk of cross-function state pollution.
///
/// Entry points accept a pre-bound <see cref="BoundBlock"/> produced by
/// the TypeChecker, so no ExprTypes lookup or _classInstanceVars heuristic
/// is needed during emission.
/// </summary>
internal sealed partial class FunctionEmitter
{
    private readonly IEmitterContext _ctx;

    // Per-function state — initialized by entry point methods, never carried across functions.
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, TypedReg> _locals = new();
    private HashSet<string> _instanceFields = new();
    private List<IrBlock> _blocks = new();
    private List<IrExceptionEntry> _exceptionTable = new();
    private Stack<(string Break, string Continue)> _loopStack = new();
    private string _curLabel = "entry";
    private List<IrInstr> _curInstrs = new();
    private bool _blockEnded;
    private string? _currentClassName;
    /// Fully-qualified name of the function currently being emitted. Used by
    /// `EmitLambdaLiteral` to compose lifted-lambda names. Set at every entry point.
    private string _currentFnQualName = "";
    /// Maps a local-function's source-level name (`Helper`) to its lifted
    /// module-level name (`Outer__Helper`). Populated by
    /// `EmitBoundLocalFunction` so call sites within the current function
    /// resolve directly to a static `Call`.
    /// See docs/design/language/closure.md §3.4 + impl-local-fn-l2 design Decision 7.
    private readonly Dictionary<string, string> _localFnLiftedNames = new();

    /// Register holding this function's `env` (Vec<Value>) when emitting a
    /// capturing lifted body — i.e. the closure's heap-allocated env passed
    /// as the first implicit parameter. -1 means the current emitter scope is
    /// not a capturing-lifted body (so `BoundCapturedIdent` is unreachable).
    /// See docs/design/language/closure.md §6 + impl-closure-l3-core design Decision 7.
    private int _envReg = -1;

    /// Maps a capture's source-level name to its slot index in `_envReg`.
    /// Populated by `EmitLifted*WithEnv` so nested lambdas inside this body
    /// can resolve their own captures transitively (Decision 6).
    private readonly Dictionary<string, int> _envCaptureIndex = new();

    // ── Debug line tracking ──────────────────────────────────────────────────
    private List<IrLineEntry> _lineTable = new();
    private int _lastLine = -1;
    private string? _sourceFile;

    internal FunctionEmitter(IEmitterContext ctx) => _ctx = ctx;

    /// Construct a sub-emitter that inherits a parent's local-fn lifting map.
    /// Used when emitting a lifted local function body so calls to *sibling*
    /// local fns (incl. self for direct recursion) resolve to their lifted
    /// names. See impl-local-fn-l2 design Decision 7.
    internal FunctionEmitter(IEmitterContext ctx, IReadOnlyDictionary<string, string> parentLiftedNames)
        : this(ctx)
    {
        foreach (var (k, v) in parentLiftedNames)
            _localFnLiftedNames[k] = v;
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    /// Emits IR for a class method. For ctors, `instanceFieldInits` (if non-null)
    /// provides the list of instance fields whose initializers should be injected
    /// at ctor entry, after base ctor call but before user body. See
    /// fix-class-field-default-init design Decision 2.
    internal IrFunction EmitMethod(
        string className, FunctionDecl method, BoundBlock body, string methodIrName,
        IReadOnlyList<FieldDecl>? instanceFieldInits = null)
    {
        bool isStatic = method.IsStatic;
        _currentClassName = className;
        _currentFnQualName = methodIrName;
        _sourceFile = method.Span.File;
        int paramOffset = isStatic ? 0 : 1;
        _nextReg = method.Params.Count + paramOffset;
        // `this` is always IrType.Ref: all z42 classes are heap-allocated reference types.
        // If value-type classes (structs on stack) are introduced, this must use the actual class IR type.
        if (!isStatic) _locals["this"] = new TypedReg(0, IrType.Ref);
        _instanceFields = isStatic ? [] : _ctx.GetClassInstanceFieldNames(className);

        StartBlock("entry");
        for (int i = 0; i < method.Params.Count; i++)
            _locals[method.Params[i].Name] = new TypedReg(i + paramOffset, ToIrType(method.Params[i].Type));

        // 2026-05-07 add-class-arity-overloading: when the IR-side class name
        // is mangled (`Foo$N`), strip the suffix to recover the bare source
        // name used for ctor identity (`method.Name == sourceClassName`).
        // Class names never contain `$` in source; the suffix is purely an
        // arity disambiguator added by the registry.
        var sourceClassName = className.Contains('$')
            ? className[..className.IndexOf('$')]
            : className;
        bool isCtor = !isStatic && method.Name == sourceClassName;
        EmitCtorChainAndFieldInits(className, method, isCtor, instanceFieldInits);

        EmitBoundBlock(body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        return BuildEmittedMethodResult(
            methodIrName, method, className, paramOffset, isCtor, isStatic);
    }

    /// Ctor entry: emit `: this(...)` chain when present, otherwise `: base(...)`
    /// and instance-field initializers. Non-ctor methods skip this entirely.
    ///   - `: this(...)`         → chained ctor handles base + field-init; emit only the call.
    ///   - implicit / `: base()` → emit base ctor call + per-field init injection.
    /// See fix-class-field-default-init Decision 2 + 2026-05-05 ctor delegation.
    private void EmitCtorChainAndFieldInits(
        string className, FunctionDecl method, bool isCtor,
        IReadOnlyList<FieldDecl>? instanceFieldInits)
    {
        if (!isCtor) return;

        bool emittedThisChain = false;
        if (method.ThisCtorArgs is { }
            && _ctx.SemanticModel.BoundThisCtorArgs.TryGetValue(method, out var boundThisArgs))
        {
            var classQual = _ctx.QualifyName(className);
            // Overload-resolve by arity within the same class.
            var thisCtorKey = ResolveBaseCtorKey(className, boundThisArgs.Count, classQual);
            var thisCtorIrName = $"{classQual}.{thisCtorKey}";
            var argRegs = new List<TypedReg> { new(0, IrType.Ref) };
            argRegs.AddRange(boundThisArgs.Select(EmitExpr));
            var dst = Alloc(IrType.Ref);
            Emit(new CallInstr(dst, thisCtorIrName, argRegs));
            emittedThisChain = true;
        }

        // Emit base constructor call at the start of derived constructors
        // (skipped when delegating via `: this(...)` — the chained ctor handles it).
        if (!emittedThisChain
            && method.BaseCtorArgs is { }
            && _ctx.SemanticModel.Classes.TryGetValue(className, out var classMeta)
            && classMeta.BaseClassName is { } baseShortName
            && _ctx.SemanticModel.BoundBaseCtorArgs.TryGetValue(method, out var boundBaseArgs))
        {
            // fix-vcall-base-class-fallback: use QualifyClassName so cross-zpkg
            // base classes (e.g. Dog : Animal where Animal is in demo.base) get
            // the correct namespace qualifier instead of the current module's ns.
            var baseQual = _ctx.QualifyClassName(baseShortName);
            var baseSimpleName = baseQual.Contains('.')
                ? baseQual[(baseQual.LastIndexOf('.') + 1)..] : baseQual;
            // Overload-resolve base ctor by arity（Z42FuncType.Params 不含 this）。
            // SemanticModel.Classes 已含 local + imported；查 base class 的 Methods
            // 字典选 `baseSimpleName` (单 ctor) 或 `baseSimpleName$N` (重载)。
            var baseCtorKey = ResolveBaseCtorKey(
                baseSimpleName, boundBaseArgs.Count, baseQual);
            var baseCtorIrName = $"{baseQual}.{baseCtorKey}";
            var argRegs = new List<TypedReg> { new(0, IrType.Ref) };
            argRegs.AddRange(boundBaseArgs.Select(EmitExpr));
            var dst = Alloc(IrType.Ref);
            Emit(new CallInstr(dst, baseCtorIrName, argRegs));
        }

        if (!emittedThisChain && instanceFieldInits is { Count: > 0 })
            EmitInstanceFieldInits(instanceFieldInits);
    }

    /// 2026-05-02 fix-class-field-default-init: 在 ctor 入口（base ctor call 之后、
    /// 用户 body 之前）按字段声明顺序注入 `this.<field> = <init-expr>`，仅对
    /// 有显式 Initializer 的字段发射；无 init 的字段由 VM ObjNew 的 type defaults
    /// 兜底（参见 design.md Decision 1/2）。隐式合成 ctor 走相同路径，body 是空
    /// BoundBlock。Caller skips this when `: this(...)` chains — chained ctor
    /// will run the field inits.
    private void EmitInstanceFieldInits(IReadOnlyList<FieldDecl> instanceFieldInits)
    {
        foreach (var field in instanceFieldInits)
        {
            if (!_ctx.SemanticModel.BoundInstanceInits.TryGetValue(field, out var initExpr))
                continue;
            var valReg = EmitExpr(initExpr);
            Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), field.Name, valReg));
        }
    }

    /// Assemble the `IrFunction` record from accumulated emitter state.
    /// Bundles the boilerplate of pulling ParamTypes / ParamModifiers /
    /// constraints / exception+line+local-var tables off the emitter.
    private IrFunction BuildEmittedMethodResult(
        string methodIrName, FunctionDecl method, string className,
        int paramOffset, bool isCtor, bool isStatic)
    {
        var retType = isCtor ? "void" : TypeName(method.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        var lineTable = _lineTable.Count > 0 ? _lineTable : null;
        var localVars = SnapshotLocalVarTable();
        int paramCount = method.Params.Count + paramOffset;
        // L3-G3a: method constraint lookup uses `{ClassName}.{MethodName}` key
        var constraints = IrGen.BuildConstraintList(
            $"{className}.{method.Name}", method.TypeParams, _ctx.SemanticModel?.FuncConstraints);
        // Spec impl-ref-out-in-runtime: surface ref/out/in modifiers on
        // each parameter into IrFunction.ParamModifiers (informational; VM
        // detects ref args at runtime via Value::Ref tag — this list lets
        // tooling render source-level shape).
        var paramMods = BuildParamModifiers(method.Params, paramOffset);
        // 1.3 split-debug-symbols Phase 4: per-parameter type names for
        // stack-trace signature decoration. Includes implicit `this` (= the
        // receiver class) at index 0 for instance methods.
        var paramTypes = new List<string>(paramCount);
        for (int i = 0; i < paramOffset; i++) paramTypes.Add(className);
        foreach (var p in method.Params) paramTypes.Add(TypeName(p.Type));
        return new IrFunction(methodIrName, paramCount, retType, "Interp", _blocks, excTable,
            IsStatic: isStatic, ParamTypes: paramTypes,
            MaxReg: _nextReg, LineTable: lineTable, LocalVarTable: localVars,
            TypeParams: method.TypeParams,
            TypeParamConstraints: constraints,
            ParamModifiers: paramMods,
            Attributes: BuildAttributeRefs(method.Attributes));
    }

    /// Spec impl-ref-out-in-runtime: convert `Param.Modifier` enum values to
    /// `byte` codes (0=None, 1=Ref, 2=Out, 3=In). Returns null when no
    /// param has a non-None modifier (saves a few bytes in zbc and keeps
    /// existing functions byte-identical).
    private static List<byte>? BuildParamModifiers(
        IReadOnlyList<Param> parms, int leadingOffset = 0)
    {
        if (parms.All(p => p.Modifier == ParamModifier.None)) return null;
        var list = new List<byte>(parms.Count + leadingOffset);
        // Leading offset reserved for `this` (instance methods); always None.
        for (int i = 0; i < leadingOffset; i++) list.Add(0);
        foreach (var p in parms)
        {
            list.Add(p.Modifier switch
            {
                ParamModifier.Ref => 1,
                ParamModifier.Out => 2,
                ParamModifier.In  => 3,
                _ => 0,
            });
        }
        return list;
    }

    /// 选择 base ctor 的 method-table key（base class 的 Methods 字典里）。
    /// 单 ctor: `baseSimpleName`；重载: `baseSimpleName$N` 按 arity 选
    /// （Z42FuncType.Params 不含 this，含 default params 时按 [Min, Total] 闭区间）。
    private string ResolveBaseCtorKey(string baseSimpleName, int argCount, string baseQual)
    {
        // baseQual 形如 "Std.Exception" → 在 SemanticModel.Classes 查 short name
        // 因为 Classes 字典 keys 是 short name（"Exception"）。
        var shortKey = baseQual.Contains('.')
            ? baseQual[(baseQual.LastIndexOf('.') + 1)..] : baseQual;
        if (!_ctx.SemanticModel.Classes.TryGetValue(shortKey, out var cls))
            return baseSimpleName; // base class 未找到 → fallback (下游报 undefined)

        bool ArityMatches(Z42FuncType sig) =>
            argCount >= sig.MinArgCount && argCount <= sig.Params.Count;

        // 单 ctor
        if (cls.Methods.TryGetValue(baseSimpleName, out var singleSym) && ArityMatches(singleSym.Signature))
            return baseSimpleName;

        // 重载
        foreach (var (key, msym) in cls.Methods)
        {
            if (key.StartsWith(baseSimpleName + "$") && ArityMatches(msym.Signature))
                return key;
        }

        // 无匹配：单 ctor 兜底
        return baseSimpleName;
    }

    internal IrFunction EmitFunction(FunctionDecl fn, BoundBlock body)
    {
        _currentClassName = null;
        _currentFnQualName = _ctx.QualifyName(fn.Name);
        _sourceFile = fn.Span.File;
        _nextReg = fn.Params.Count;

        StartBlock("entry");
        for (int i = 0; i < fn.Params.Count; i++)
            _locals[fn.Params[i].Name] = new TypedReg(i, ToIrType(fn.Params[i].Type));

        EmitBoundBlock(body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retType = fn.ReturnType is VoidType ? "void" : TypeName(fn.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        var lineTable = _lineTable.Count > 0 ? _lineTable : null;
        var localVars = SnapshotLocalVarTable();
        var constraints = IrGen.BuildConstraintList(
            fn.Name, fn.TypeParams, _ctx.SemanticModel?.FuncConstraints);
        var paramMods = BuildParamModifiers(fn.Params);
        var paramTypes = fn.Params.Select(p => TypeName(p.Type)).ToList();
        return new IrFunction(_ctx.QualifyName(fn.Name), fn.Params.Count, retType,
            "Interp", _blocks, excTable, ParamTypes: paramTypes,
            MaxReg: _nextReg, LineTable: lineTable, LocalVarTable: localVars,
            TypeParams: fn.TypeParams,
            TypeParamConstraints: constraints,
            ParamModifiers: paramMods,
            Attributes: BuildAttributeRefs(fn.Attributes));
    }

    /// C3b add-attribute-reflection-methods: map a declaration's user attributes
    /// to IR refs — (attribute type qualified name, synthesized factory func
    /// qualified name). Mirrors <see cref="IrGen.EmitClassDesc"/>'s class path.
    /// Returns null when there are no attributes carrying a factory.
    private List<IrAttributeRef>? BuildAttributeRefs(List<AttributeApp>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return null;
        var refs = attrs
            .Where(a => a.FactoryFunc is not null)
            .Select(a => new IrAttributeRef(
                _ctx.QualifyClassName(a.Name),
                _ctx.QualifyName(a.FactoryFunc!)))
            .ToList();
        return refs.Count == 0 ? null : refs;
    }
}
