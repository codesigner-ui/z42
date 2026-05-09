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

    public IrModule Generate(CompilationUnit cu)
    {
        _namespace = cu.Namespace;

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
        // get registered under their source namespace (`Std.int.op_Add`), not the
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
            foreach (var (methodKey, sig) in ct.Methods)
            {
                _funcSignatures[$"{localKey}.{methodKey}"] = sig;
                if (importedKey != localKey)
                    _funcSignatures[$"{importedKey}.{methodKey}"] = sig;
            }
            foreach (var (methodKey, sig) in ct.StaticMethods)
            {
                _funcSignatures[$"{localKey}.{methodKey}"] = sig;
                if (importedKey != localKey)
                    _funcSignatures[$"{importedKey}.{methodKey}"] = sig;
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
        // See docs/design/closure.md §6 + design.md Decision 1.
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

        // Phase 3 S2 step 2 (tokenize-ir-and-zbc-bump, 2026-05-09): build
        // TokenAllocator as a sibling output. Walks the just-emitted IrModule:
        // intra-module classes / functions / static fields → registered;
        // cross-zpkg refs encountered in instructions → DiscoverImport.
        // After Build(), the allocator is ready for ZbcWriter v1.0 to consume
        // (S3). C# IR record fields stay String (option β decision).
        _allocator = BuildTokenAllocator(module);

        return module;
    }

    // ── TokenAllocator construction (Phase 3 S2 step 2) ──────────────────

    /// <summary>Phase 3 sibling output: walks the emitted IrModule and builds a
    /// finalized <see cref="TokenAllocator"/>. Available via <see cref="Allocator"/>
    /// after <see cref="Generate"/> returns. Null before Generate runs.</summary>
    private TokenAllocator? _allocator;

    /// <summary>Phase 3 (tokenize-ir-and-zbc-bump): finalized TokenAllocator
    /// for this module. Populated by <see cref="Generate"/>; null until then.
    /// ZbcWriter (S3) reads this to emit v1.0 tokens + IMPT section.</summary>
    public TokenAllocator? Allocator => _allocator;

    private static TokenAllocator BuildTokenAllocator(IrModule module)
    {
        var allocator = new TokenAllocator();

        // ── Pass 1: register intra-module decls ───────────────────────────
        var localClassNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
        {
            allocator.RegisterClass(cls.Name);
            localClassNames.Add(cls.Name);
        }
        var localFuncNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fn in module.Functions)
        {
            allocator.RegisterMethod(fn.Name);
            localFuncNames.Add(fn.Name);
        }

        // Static fields aren't a top-level decl list; infer from StaticGet/Set
        // sites whose owning class FQ is local. Collect cross-zpkg static
        // fields as imports.
        // (Same loop also collects cross-zpkg method/type imports.)

        // ── Pass 2: scan instructions for refs (local registrations of
        //           static fields + cross-zpkg DiscoverImport for everything) ─
        foreach (var fn in module.Functions)
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
        {
            switch (instr)
            {
                case CallInstr c:
                    if (!localFuncNames.Contains(c.Func))
                        allocator.DiscoverImport(ImportKind.Method, c.Func);
                    break;
                case LoadFnInstr lf:
                    if (!localFuncNames.Contains(lf.Func))
                        allocator.DiscoverImport(ImportKind.Method, lf.Func);
                    break;
                case LoadFnCachedInstr lfc:
                    if (!localFuncNames.Contains(lfc.Func))
                        allocator.DiscoverImport(ImportKind.Method, lfc.Func);
                    break;
                case MkClosInstr mk:
                    if (!localFuncNames.Contains(mk.FuncName))
                        allocator.DiscoverImport(ImportKind.Method, mk.FuncName);
                    break;
                case ObjNewInstr on:
                    if (!localClassNames.Contains(on.ClassName))
                        allocator.DiscoverImport(ImportKind.Type, on.ClassName);
                    if (!localFuncNames.Contains(on.CtorName))
                        allocator.DiscoverImport(ImportKind.Method, on.CtorName);
                    if (on.TypeArgs is not null)
                        foreach (var ta in on.TypeArgs)
                            if (LooksLikeTypeName(ta) && !localClassNames.Contains(ta))
                                allocator.DiscoverImport(ImportKind.Type, ta);
                    break;
                case IsInstanceInstr ii:
                    if (!localClassNames.Contains(ii.ClassName))
                        allocator.DiscoverImport(ImportKind.Type, ii.ClassName);
                    break;
                case AsCastInstr ac:
                    if (!localClassNames.Contains(ac.ClassName))
                        allocator.DiscoverImport(ImportKind.Type, ac.ClassName);
                    break;
                case StaticGetInstr sg:
                    RegisterStaticFieldRef(allocator, localClassNames, sg.Field);
                    break;
                case StaticSetInstr ss:
                    RegisterStaticFieldRef(allocator, localClassNames, ss.Field);
                    break;
                // BuiltinInstr.Name → not tokenized via allocator (closed set
                //   resolved by runtime BUILTINS table).
                // VCallInstr / FieldGet/Set / LoadFieldAddr → receiver-type-
                //   dependent String, IC-cached at runtime; not in allocator.
                // CallNativeInstr / CallNativeVtableInstr → native interop,
                //   separate concern; not in allocator.
            }
        }

        allocator.Build();
        return allocator;
    }

    /// <summary>Heuristic: TypeArgs entries that "look like" a type name
    /// (contain '.' indicating FQ class) are candidates for import discovery;
    /// primitive type tags (e.g. "int", "f64") and unresolved type-param
    /// names (e.g. "T") are not registered as types.</summary>
    private static bool LooksLikeTypeName(string typeArg) =>
        typeArg.Contains('.') && !IsPrimitiveTag(typeArg);

    private static bool IsPrimitiveTag(string s) => s switch
    {
        "int" or "long" or "short" or "byte" or "sbyte"
        or "ushort" or "uint" or "ulong"
        or "i8" or "i16" or "i32" or "i64"
        or "u8" or "u16" or "u32" or "u64"
        or "isize" or "usize"
        or "double" or "float" or "f32" or "f64"
        or "bool" or "char" or "str" or "string"
        or "void" or "object" => true,
        _ => false,
    };

    /// <summary>Register a static field reference. Local if owning class is
    /// in <paramref name="localClassNames"/>; otherwise discovered as import.</summary>
    private static void RegisterStaticFieldRef(
        TokenAllocator allocator, HashSet<string> localClassNames, string fieldFqName)
    {
        // fieldFqName format: "Owner.Class.fieldName" — strip last segment.
        var dot = fieldFqName.LastIndexOf('.');
        if (dot < 0)
        {
            // No owner class; treat as local (rare, defensively register local).
            allocator.RegisterStaticField(fieldFqName);
            return;
        }
        var ownerClass = fieldFqName[..dot];
        if (localClassNames.Contains(ownerClass))
            allocator.RegisterStaticField(fieldFqName);
        else
            allocator.DiscoverImport(ImportKind.StaticField, fieldFqName);
    }

    /// <summary>R1 — walk FunctionDecls (top-level + class methods) and emit one
    /// <see cref="TestEntry"/> per function decorated with z42.test.* attributes.
    /// `method_id` is resolved by name lookup into the post-emit functions list.
    /// </summary>
    private List<TestEntry> BuildTestIndex(CompilationUnit cu, List<IrFunction> functions)
    {
        var entries = new List<TestEntry>();

        // Build name → index lookup once (functions list is finalized at this point).
        var fnIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < functions.Count; i++)
            fnIndexByName.TryAdd(functions[i].Name, i);

        // Top-level test functions
        foreach (var fn in cu.Functions)
        {
            if (fn.TestAttributes is null || fn.TestAttributes.Count == 0) continue;
            if (!fnIndexByName.TryGetValue(QualifyName(fn.Name), out var idx)) continue;
            entries.Add(BuildTestEntry(idx, fn.TestAttributes));
        }

        // Class methods (prefix with class name, mirror EmitMethod naming)
        foreach (var cls in cu.Classes)
            foreach (var m in cls.Methods)
            {
                if (m.TestAttributes is null || m.TestAttributes.Count == 0) continue;
                var qualClass = ((IEmitterContext)this).QualifyClassName(cls.Name);
                // Try with arity suffix first (overload-aware), fall back to bare name.
                string arityKey = $"{qualClass}.{m.Name}${m.Params.Count}";
                string bareKey  = $"{qualClass}.{m.Name}";
                if (fnIndexByName.TryGetValue(arityKey, out var idx)
                    || fnIndexByName.TryGetValue(bareKey, out idx))
                    entries.Add(BuildTestEntry(idx, m.TestAttributes));
            }

        return entries;
    }

    /// <summary>Reduce a list of parsed test attributes on one function into a
    /// single <see cref="TestEntry"/>. Strings (Skip reason/platform/feature)
}
