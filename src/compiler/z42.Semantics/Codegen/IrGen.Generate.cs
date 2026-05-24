using System.IO;
using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{

    // ── Public API ─────────────────────────────────────────────────────────────

    public IrModule Generate(CompilationUnit cu, string? sourcePath = null)
    {
        _namespace = cu.Namespace;
        // fix-multi-file-static-init (2026-05-15): derive a CU-unique stem
        // from the source filename so multi-file namespaces don't all emit
        // the same `<ns>.__static_init__` (which would collide on zpkg load).
        _cuStem = sourcePath is null ? null
            : Path.GetFileNameWithoutExtension(sourcePath);

        if (_semanticModel is null)
            throw new InvalidOperationException(
                "SemanticModel is required for code generation.");

        // ── Populate from SemanticModel (no overload re-detection) ────────────

        foreach (var (key, val) in _semanticModel.EnumConstants)
            _enumConstants[key] = val;

        foreach (var (shortName, ct) in _semanticModel.Classes)
        {
            var qualName = QualifyName(shortName);
            var qualBase = ct.BaseClassName is not null ? QualifyName(ct.BaseClassName) : null;
            _classRegistry.Register(qualName, ct, qualBase);
        }

        _topLevelFunctionNames = _semanticModel.Funcs.Keys.ToHashSet();

        // ── Param lists from AST (for FillDefaults BoundExpr key lookup) ─────

        foreach (var fn in cu.Functions)
            _funcParams[QualifyName(fn.Name)] = fn.Params;
        foreach (var cls in cu.Classes)
        {
            // 2026-05-07 add-class-arity-overloading: use IR short name (mangled
            // when collision) for the qualified key so coexisting same-source-
            // name classes (`Foo` + `Foo<R>`) don't collide in `_funcParams`.
            int clsArity = cls.TypeParams?.Count ?? 0;
            string clsLookupKey = clsArity > 0
                && _semanticModel.Classes.TryGetValue($"{cls.Name}${clsArity}", out var manglee)
                && manglee.HasArityMangle
                    ? $"{cls.Name}${clsArity}"
                    : cls.Name;
            if (!_semanticModel.Classes.TryGetValue(clsLookupKey, out var ct))
                throw new InvalidOperationException(
                    $"IrGen: class `{cls.Name}` not found in SemanticModel (TypeChecker bug)");
            var qualName = QualifyName(ct.IrName);
            foreach (var m in cls.Methods)
            {
                string arityKey = $"{m.Name}${m.Params.Count}";
                bool isOverloaded = m.IsStatic
                    ? ct.StaticMethods.ContainsKey(arityKey)
                    : ct.Methods.ContainsKey(arityKey);
                string key = isOverloaded
                    ? $"{qualName}.{arityKey}"
                    : $"{qualName}.{m.Name}";
                _funcParams[key] = m.Params;
            }
        }
        // L3 extern impl (Change 1): register impl methods under the target class's qualified name.
        // L3-Impl2: use QualifyClassName so imported targets (e.g. `int` from z42.core)
        // get registered under their source namespace (`Std.Int32.op_Add`), not the
        // current package namespace. Local targets are unaffected (QualifyClassName
        // returns QualifyName when class is local).
        foreach (var impl in cu.Impls)
        {
            if (impl.TargetType is not NamedType targetNt) continue;
            var qualName = ((IEmitterContext)this).QualifyClassName(targetNt.Name);
            foreach (var m in impl.Methods)
                _funcParams[$"{qualName}.{m.Name}"] = m.Params;
        }

        // 2026-05-04 fix-default-param-cross-cu (D-9)：注册所有方法 / 顶层函数
        // 的 Z42FuncType 签名（local + imported）。FillDefaults fallback 用此
        // 表对跨 CU 调用 emit type-default const 填充缺位参数。
        //
        // 双 key 注册：本地 QualifyName（与 ClassRegistry / FuncParams 同款键）
        // + 跨 CU QualifyClassName（与 DepIndex.QualifiedName 同款键）。
        foreach (var (className, ct) in _semanticModel!.Classes)
        {
            var localKey    = QualifyName(className);
            var importedKey = ((IEmitterContext)this).QualifyClassName(className);
            foreach (var (methodKey, msym) in ct.Methods)
            {
                _funcSignatures[$"{localKey}.{methodKey}"] = msym.Signature;
                if (importedKey != localKey)
                    _funcSignatures[$"{importedKey}.{methodKey}"] = msym.Signature;
            }
            foreach (var (methodKey, msym) in ct.StaticMethods)
            {
                _funcSignatures[$"{localKey}.{methodKey}"] = msym.Signature;
                if (importedKey != localKey)
                    _funcSignatures[$"{importedKey}.{methodKey}"] = msym.Signature;
            }
        }
        foreach (var (funcName, sig) in _semanticModel.Funcs)
        {
            _funcSignatures[funcName] = sig;
            _funcSignatures[QualifyName(funcName)] = sig;
        }

        // ── Emit ──────────────────────────────────────────────────────────────

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();

        if (cu.Classes.Any(cls => cls.Fields.Any(f => f.IsStatic)))
            functions.Add(new FunctionEmitter(this).EmitStaticInit(cu));

        // 2026-05-02 fix-class-field-default-init: 为每个 class 收集"有显式
        // initializer 的实例字段"列表，并在该类每个 ctor 上注入 init。无显式
        // ctor 但任一字段（含本类或本地祖先链中任一节点）有 init →
        // 合成无参隐式 ctor，按祖先 → 自身顺序内联所有字段 init。参见 design.md
        // Decision 2/3 + 备注：由于 z42 当前模型不自动调用 base ctor，合成 ctor
        // 不依赖 base call，而是直接内联本地祖先的 field init 表达式。
        // 2026-05-07 add-class-arity-overloading: when same source name has
        // arity-disjoint siblings (e.g. `class Foo` + `class Foo<R>`), keying
        // by bare name collides. Use the IrName route via SemanticModel to
        // produce stable arity-aware keys for the lookup table.
        string IrKeyOf(ClassDecl c)
        {
            int arity = c.TypeParams?.Count ?? 0;
            if (arity > 0
                && _semanticModel!.Classes.TryGetValue($"{c.Name}${arity}", out var manglee)
                && manglee.HasArityMangle)
                return manglee.IrName;
            return c.Name;
        }
        var localClassByName = cu.Classes.ToDictionary(IrKeyOf, c => c);
        foreach (var cls in cu.Classes)
        {
            var ownFieldInits = cls.Fields
                .Where(f => !f.IsStatic && f.Initializer != null)
                .ToList();

            // 2026-05-07 add-class-arity-overloading: pass the IR-side short name
            // (mangled `Foo$N` for collision case, bare `Name` otherwise) so the
            // method's qualified IR name keys remain unique when two same-source-
            // name classes coexist.
            var clsShortIr = ClassIrShortName(cls);
            bool hasExplicitCtor = false;
            foreach (var m in cls.Methods.Where(m => !m.IsAbstract))
            {
                bool isCtor = !m.IsStatic && m.Name == cls.Name;
                if (isCtor) hasExplicitCtor = true;
                // 显式 ctor 仅注入"本类自己"的字段 init（保留现有 z42 模型：
                // 用户显式 ctor 必须自己 `: base(...)` 触发父类 init）。
                functions.Add(EmitMethod(
                    clsShortIr, m, cls.ClassNativeDefaults,
                    isCtor ? ownFieldInits : null));
            }

            if (!hasExplicitCtor)
            {
                // 合成 ctor 需要看祖先链：只要本类或任一本地祖先有 field init，
                // 就需要合成；并且 init 列表覆盖整条链（祖先在前、自身在后）。
                var chainInits = CollectChainFieldInits(cls, localClassByName);
                if (chainInits.Count > 0)
                    functions.Add(EmitImplicitCtor(cls, chainInits));
            }
        }
        // L3 extern impl (Change 1): emit impl method bodies under the target class.
        foreach (var impl in cu.Impls)
        {
            if (impl.TargetType is not NamedType implTargetNt) continue;
            functions.AddRange(impl.Methods.Where(m => !m.IsAbstract)
                .Select(m => EmitMethod(implTargetNt.Name, m)));
        }
        functions.AddRange(cu.Functions.Select(EmitFunction));

        // impl-lambda-l2: append all lambda lifted functions registered during emission.
        // See docs/design/language/closure.md §6 + design.md Decision 1.
        functions.AddRange(_liftedFunctions);

        // R1: collect TestIndex from FunctionDecl.TestAttributes across top-level
        // and class-method scopes. method_id is the index into `functions` (post-
        // emit, in declaration order). We look up each test-decorated FunctionDecl
        // by its qualified IR-side name to find the matching emitted function.
        var testIndex = BuildTestIndex(cu, functions);

        var module = new IrModule(cu.Namespace ?? "main", _strings, classes, functions,
            TestIndex: testIndex.Count > 0 ? testIndex : null,
            FuncRefCacheSlotCount: _nextFuncRefSlotId);
        IrVerifier.VerifyOrThrow(module);

        // Phase 3 S3b (tokenize-ir-and-zbc-bump, 2026-05-09 redesigned):
        // build TokenAllocator as a sibling output. Local IDs come from
        // module.Functions / module.Classes insertion order; cross-zpkg
        // refs flow through STRS pool at ZbcWriter time (no IMPT extension).
        _allocator = TokenAllocator.FromModule(module);

        return module;
    }

    /// <summary>Phase 3 (tokenize-ir-and-zbc-bump): finalized TokenAllocator
    /// for this module. Populated by <see cref="Generate"/>; null until then.
    /// ZbcWriter consumes this to emit v1.0 token-encoded IR fields.</summary>
    private TokenAllocator? _allocator;
    public TokenAllocator? Allocator => _allocator;

    /// <summary>R1 — walk FunctionDecls (top-level + class methods) and emit one
    /// <see cref="TestEntry"/> per function decorated with z42.test.* attributes.
    /// `method_id` is resolved by name lookup into the post-emit functions list.
    ///
    /// split-symbol-from-type Phase 5 (2026-05-10): for class methods, read
    /// TestAttributes via <see cref="Symbols.IMethodSymbol.TestAttributes"/>
    /// (single source of truth at the Symbol layer) rather than re-walking
    /// `cu.Classes[i].Methods[j].TestAttributes` AST. cu.Classes iteration is
    /// kept for source-order stability and local-class scoping (imported test
    /// methods are out of scope). Top-level functions still walk cu.Functions
    /// because top-level fns aren't yet wrapped in IMethodSymbol (out of spec).
    /// </summary>
    private List<TestEntry> BuildTestIndex(CompilationUnit cu, List<IrFunction> functions)
    {
        var entries = new List<TestEntry>();

        // Build name → index lookup once (functions list is finalized at this point).
        var fnIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < functions.Count; i++)
            fnIndexByName.TryAdd(functions[i].Name, i);

        // Top-level test functions (AST path — no symbol layer for top-level fns).
        foreach (var fn in cu.Functions)
        {
            if (fn.TestAttributes is null || fn.TestAttributes.Count == 0) continue;
            if (!fnIndexByName.TryGetValue(QualifyName(fn.Name), out var idx)) continue;
            entries.Add(BuildTestEntry(idx, fn.TestAttributes));
        }

        // Class methods — read TestAttributes from IMethodSymbol (Symbol layer).
        foreach (var cls in cu.Classes)
        {
            if (!_semanticModel!.Classes.TryGetValue(cls.Name, out var ct)) continue;
            foreach (var m in cls.Methods)
            {
                // Symbol-table key: arity-mangled when overloaded, bare otherwise.
                // Mirror SymbolCollector.Classes.cs:200-256 regName logic by trying
                // both. SymbolCollector stores instance methods in ct.Methods and
                // statics in ct.StaticMethods; check both buckets.
                var arityKey = $"{m.Name}${m.Params.Count}";
                var msym = ct.Methods.GetValueOrDefault(arityKey)
                        ?? ct.Methods.GetValueOrDefault(m.Name)
                        ?? ct.StaticMethods.GetValueOrDefault(arityKey)
                        ?? ct.StaticMethods.GetValueOrDefault(m.Name);
                if (msym?.TestAttributes is not { Count: > 0 } attrs) continue;

                var qualClass = ((IEmitterContext)this).QualifyClassName(cls.Name);
                string fullArityKey = $"{qualClass}.{m.Name}${m.Params.Count}";
                string fullBareKey  = $"{qualClass}.{m.Name}";
                if (fnIndexByName.TryGetValue(fullArityKey, out var idx)
                    || fnIndexByName.TryGetValue(fullBareKey, out idx))
                    entries.Add(BuildTestEntry(idx, attrs));
            }
        }

        return entries;
    }

    /// <summary>Reduce a list of parsed test attributes on one function into a
    /// single <see cref="TestEntry"/>. Strings (Skip reason/platform/feature)
}
