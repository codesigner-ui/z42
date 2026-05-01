using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Code generator: emits an IrModule from a CompilationUnit + SemanticModel.
///
/// Module-level state (class/function maps, string pool) lives here; most of it
/// is now populated from <see cref="SemanticModel"/> rather than re-traversing
/// the AST.  Per-function emission is delegated to <see cref="FunctionEmitter"/>.
/// </summary>
public sealed class IrGen : IEmitterContext
{
    private readonly DependencyIndex _depIndex;
    internal readonly LanguageFeatures _features;
    private SemanticModel? _semanticModel;

    // Dependency namespaces used by this compilation unit (populated during codegen).
    private readonly HashSet<string> _usedDepNamespaces = new();

    /// The set of dependency namespaces that were actually called during this compilation.
    public IReadOnlySet<string> UsedDepNamespaces => _usedDepNamespaces;

    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
    private string? _namespace;

    // ── Derived from SemanticModel ───────────────────────────────────────────
    private readonly ClassRegistry _classRegistry = new();
    private HashSet<string> _topLevelFunctionNames = new();
    private readonly Dictionary<string, long> _enumConstants = new();
    // Param lists still come from AST (needed for FillDefaults BoundExpr lookup).
    private Dictionary<string, IReadOnlyList<Param>> _funcParams = new();

    // Lifted lambda functions accumulated during emission (impl-lambda-l2).
    // See docs/design/closure.md §6 + design.md Decision 1.
    private readonly List<IrFunction> _liftedFunctions = new();
    private readonly Dictionary<string, int> _lambdaCounters = new();

    // ── IEmitterContext explicit implementation ──────────────────────────────
    ClassRegistry IEmitterContext.ClassRegistry => _classRegistry;
    SemanticModel IEmitterContext.SemanticModel => _semanticModel!;
    HashSet<string> IEmitterContext.TopLevelFunctionNames => _topLevelFunctionNames;
    IReadOnlyDictionary<string, long> IEmitterContext.EnumConstants => _enumConstants;
    IReadOnlyDictionary<string, IReadOnlyList<Param>> IEmitterContext.FuncParams => _funcParams;
    DependencyIndex IEmitterContext.DepIndex => _depIndex;
    void IEmitterContext.TrackDepNamespace(string ns) => _usedDepNamespaces.Add(ns);
    string IEmitterContext.QualifyName(string name) => QualifyName(name);
    int IEmitterContext.Intern(string s) => Intern(s);
    HashSet<string> IEmitterContext.GetClassInstanceFieldNames(string className) =>
        GetClassInstanceFieldNames(className);
    string? IEmitterContext.FindVcallParamsKey(string methodName, int suppliedArgCount) =>
        FindVcallParamsKey(methodName, suppliedArgCount);
    string? IEmitterContext.TryGetStaticFieldKey(string className, string fieldName) =>
        TryGetStaticFieldKey(className, fieldName);
    IReadOnlyDictionary<string, string> IEmitterContext.ImportedClassNamespaces =>
        _semanticModel?.ImportedClassNamespaces ?? new Dictionary<string, string>();
    void IEmitterContext.RegisterLiftedFunction(IrFunction fn) => _liftedFunctions.Add(fn);
    int IEmitterContext.NextLambdaIndex(string containerName)
    {
        if (_lambdaCounters.TryGetValue(containerName, out var idx))
        {
            _lambdaCounters[containerName] = idx + 1;
            return idx;
        }
        _lambdaCounters[containerName] = 1;
        return 0;
    }
    string IEmitterContext.QualifyClassName(string className)
    {
        // L3-G4d: local classes shadow imported ones. If a class exists in the semantic
        // model AND is not flagged as imported, use the current module namespace. Only
        // fall back to the imported namespace when the name truly refers to an import.
        var sem = _semanticModel;
        if (sem is not null
            && sem.Classes.ContainsKey(className)
            && !sem.ImportedClassNames.Contains(className))
            return QualifyName(className);
        return sem?.ImportedClassNamespaces.TryGetValue(className, out var ns) == true
            ? $"{ns}.{className}" : QualifyName(className);
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    public IrGen(DependencyIndex? depIndex = null, LanguageFeatures? features = null,
                 SemanticModel? semanticModel = null)
    {
        _depIndex       = depIndex ?? DependencyIndex.Empty;
        _features       = features ?? LanguageFeatures.Phase1;
        _semanticModel  = semanticModel;
    }

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
            var qualName = QualifyName(cls.Name);
            if (!_semanticModel.Classes.TryGetValue(cls.Name, out var ct))
                throw new InvalidOperationException(
                    $"IrGen: class `{cls.Name}` not found in SemanticModel (TypeChecker bug)");
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

        // ── Emit ──────────────────────────────────────────────────────────────

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();

        if (cu.Classes.Any(cls => cls.Fields.Any(f => f.IsStatic)))
            functions.Add(new FunctionEmitter(this).EmitStaticInit(cu));

        foreach (var cls in cu.Classes)
            functions.AddRange(cls.Methods.Where(m => !m.IsAbstract)
                .Select(m => EmitMethod(cls.Name, m, cls.ClassNativeDefaults)));
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
            TestIndex: testIndex.Count > 0 ? testIndex : null);
        IrVerifier.VerifyOrThrow(module);
        return module;
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
    /// are interned into the module's string pool with 1-based indices.</summary>
    private TestEntry BuildTestEntry(int methodId, List<TestAttribute> attrs)
    {
        var kind         = TestEntryKind.Test;          // default if no primary kind seen
        var flags        = TestFlags.None;
        int reasonIdx    = 0;
        int platformIdx  = 0;
        int featureIdx   = 0;
        int expectedThrowTypeIdx = 0;

        foreach (var attr in attrs)
        {
            switch (attr.Name)
            {
                case "Test":      kind = TestEntryKind.Test;      break;
                case "Benchmark": kind = TestEntryKind.Benchmark; break;
                case "Setup":     kind = TestEntryKind.Setup;     break;
                case "Teardown":  kind = TestEntryKind.Teardown;  break;
                case "Ignore":    flags |= TestFlags.Ignored;     break;
                case "Skip":
                    flags |= TestFlags.Skipped;
                    if (attr.NamedArgs is not null)
                    {
                        if (attr.NamedArgs.TryGetValue("reason", out var reason))
                            reasonIdx = Intern(reason) + 1;     // 1-based
                        if (attr.NamedArgs.TryGetValue("platform", out var platform))
                            platformIdx = Intern(platform) + 1;
                        if (attr.NamedArgs.TryGetValue("feature", out var feature))
                            featureIdx = Intern(feature) + 1;
                    }
                    break;
                case "ShouldThrow":
                    flags |= TestFlags.ShouldThrow;
                    // R4.B — TypeArg null is rejected by TestAttributeValidator
                    // (E0913); guard here defensively for non-validated paths.
                    //
                    // A3 — emit the user-written type plus its ancestor short
                    // names as a `;`-delimited chain ("TestFailure;Exception").
                    // The runner accepts a match against any entry, giving
                    // inheritance-aware ShouldThrow without any TIDX layout
                    // change or cross-module class loading at runtime.
                    if (attr.TypeArg is not null)
                        expectedThrowTypeIdx = Intern(BuildShouldThrowChain(attr.TypeArg)) + 1;
                    break;
            }
        }

        return new TestEntry(
            MethodId:             methodId,
            Kind:                 kind,
            Flags:                flags,
            SkipReasonStrIdx:     reasonIdx,
            SkipPlatformStrIdx:   platformIdx,
            SkipFeatureStrIdx:    featureIdx,
            ExpectedThrowTypeIdx: expectedThrowTypeIdx,          // R4.B
            TestCases:            Array.Empty<TestCase>());     // R4 (TestCase parser pending)
    }

    /// A3 — Build the `;`-delimited expected-throw chain for `[ShouldThrow<E>]`.
    ///
    /// Emits <c>E</c> followed by every visible class whose inheritance chain
    /// passes through <c>E</c> — i.e. <c>E</c> + its descendants. The runner
    /// splits on ';' and matches against any entry, so
    /// <c>[ShouldThrow&lt;Exception&gt;]</c> catching a <c>TestFailure</c> throw
    /// passes because <c>TestFailure</c> appears in the chain (compile-time
    /// inclusion list, not runtime walk).
    ///
    /// Limited to classes visible in this CU's <see cref="SemanticModel.Classes"/>
    /// (which includes imports brought in by <c>using</c> directives). Classes
    /// in non-imported zpkg dependencies are not enumerated; for those the
    /// runner falls back to direct matching.
    private string BuildShouldThrowChain(string typeArg)
    {
        var chain = new List<string> { typeArg };
        if (_semanticModel is null) return typeArg;

        foreach (var (name, cls) in _semanticModel.Classes)
        {
            if (name == typeArg) continue;
            if (IsDescendantOf(cls, typeArg)) chain.Add(name);
        }
        return string.Join(';', chain);
    }

    /// Whether <paramref name="cls"/> derives transitively from
    /// <paramref name="ancestorShortName"/>. Walks <c>BaseClassName</c> chain;
    /// cycle-guarded.
    private bool IsDescendantOf(Z42ClassType cls, string ancestorShortName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = cls;
        while (current is not null && visited.Add(current.Name))
        {
            if (current.BaseClassName is null) return false;
            var baseFq = current.BaseClassName;
            var baseShort = baseFq.Contains('.')
                ? baseFq[(baseFq.LastIndexOf('.') + 1)..]
                : baseFq;
            if (baseShort == ancestorShortName) return true;
            if (_semanticModel?.Classes.TryGetValue(baseShort, out var next) != true)
                return false;
            current = next;
        }
        return false;
    }

    // ── Per-function delegation ──────────────────────────────────────────────

    private IrFunction EmitMethod(string className, FunctionDecl method,
        Tier1NativeBinding? classNativeDefaults = null)
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
        return new FunctionEmitter(this).EmitMethod(className, method, body, methodIrName);
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

    // ── Class descriptors ────────────────────────────────────────────────────

    private IrClassDesc EmitClassDesc(ClassDecl cls)
    {
        var baseClass = cls.BaseClass is not null
            ? QualifyName(cls.BaseClass)
            : (cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name))
                ? null : "Std.Object";
        return new(QualifyName(cls.Name), baseClass,
            cls.Fields.Where(f => !f.IsStatic)
                .Select(f => new IrFieldDesc(f.Name, TypeName(f.Type))).ToList(),
            cls.TypeParams?.ToList(),
            BuildConstraintList(cls.Name, cls.TypeParams, _semanticModel?.ClassConstraints));
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

    // ── Module-level helpers ─────────────────────────────────────────────────

    internal string QualifyName(string name) =>
        _namespace is null ? name : $"{_namespace}.{name}";

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
