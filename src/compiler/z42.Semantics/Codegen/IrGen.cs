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
public sealed partial class IrGen : IEmitterContext
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

    // 2026-05-04 fix-default-param-cross-cu (D-9)：跨 CU 方法签名注册表。
    // imported 方法没有 AST `Param`（FuncParams 不覆盖），但 `Z42FuncType` 携带
    // RequiredCount + Params 类型信息够用 type-default fallback。同时注册
    // local 方法 / 顶层 func，保证 FillDefaults 单一查找路径。
    private Dictionary<string, Z42FuncType> _funcSignatures = new(StringComparer.Ordinal);

    // Lifted lambda functions accumulated during emission (impl-lambda-l2).
    // See docs/design/closure.md §6 + design.md Decision 1.
    private readonly List<IrFunction> _liftedFunctions = new();
    private readonly Dictionary<string, int> _lambdaCounters = new();

    // 2026-05-02 add-method-group-conversion (D1b): module-level FuncRef
    // cache slot allocator. Same fully-qualified function name shares one
    // slot across all call sites (design Decision 1).
    private readonly Dictionary<string, int> _funcRefSlots = new(StringComparer.Ordinal);
    private int _nextFuncRefSlotId = 0;

    // ── IEmitterContext explicit implementation ──────────────────────────────
    ClassRegistry IEmitterContext.ClassRegistry => _classRegistry;
    SemanticModel IEmitterContext.SemanticModel => _semanticModel!;
    HashSet<string> IEmitterContext.TopLevelFunctionNames => _topLevelFunctionNames;
    IReadOnlyDictionary<string, long> IEmitterContext.EnumConstants => _enumConstants;
    IReadOnlyDictionary<string, IReadOnlyList<Param>> IEmitterContext.FuncParams => _funcParams;
    bool IEmitterContext.TryGetMethodSignature(string qualifiedName, out Z42FuncType sig)
    {
        if (_funcSignatures.TryGetValue(qualifiedName, out var found)) { sig = found; return true; }
        sig = null!;
        return false;
    }
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

    int IEmitterContext.GetOrAllocFuncRefSlot(string fqName)
    {
        if (_funcRefSlots.TryGetValue(fqName, out var id)) return id;
        var allocated = _nextFuncRefSlotId++;
        _funcRefSlots[fqName] = allocated;
        return allocated;
    }

    // 2026-05-04 D-1b instance method group conversion: thunk cache keyed by
    // `<qualifiedClassName>.<methodName>$<arity>`，value 是 thunk 的 fully-qualified
    // name（已注册到 _liftedFunctions）。同一 (class, method, arity) 复用 thunk。
    private readonly Dictionary<string, string> _instanceMethodThunks = new(StringComparer.Ordinal);

    string IEmitterContext.GetOrCreateInstanceMethodThunk(
        string qualifiedClassName, string methodName, Z42FuncType signature)
    {
        int arity = signature.Params.Count;
        var cacheKey = $"{qualifiedClassName}.{methodName}${arity}";
        if (_instanceMethodThunks.TryGetValue(cacheKey, out var cached))
            return cached;

        // Thunk 名形如 `<currentNs>.__mg_thunk_<safeClass>_<method>$<arity>__`
        // safeClass 把 namespace 点替换为 `_`，避免 lookup 时 namespace 解析二义性。
        var safeClass = qualifiedClassName.Replace('.', '_');
        var thunkName = $"{QualifyName($"__mg_thunk_{safeClass}_{methodName}${arity}__")}";

        // 构建 thunk IrFunction：
        //   reg 0     = env (Vec<Value>) — env[0] is the receiver
        //   reg 1..N  = user args (matching method's signature minus `this`)
        //   reg N+1   = idxReg (const i32 = 0)
        //   reg N+2   = recvReg (env[0])
        //   reg N+3   = retReg (vcall result)
        var instrs   = new List<IrInstr>();
        var idxReg   = new TypedReg(arity + 1, IrType.I32);
        var recvReg  = new TypedReg(arity + 2, IrType.Ref);
        instrs.Add(new ConstI32Instr(idxReg, 0));
        instrs.Add(new ArrayGetInstr(recvReg, new TypedReg(0, IrType.Ref), idxReg));

        var argRegs = new List<TypedReg>();
        for (int i = 0; i < arity; i++)
            argRegs.Add(new TypedReg(1 + i, FunctionEmitter.ToIrType(signature.Params[i])));

        IrTerminator term;
        int maxReg;
        if (signature.Ret == Z42Type.Void)
        {
            // void: emit VCall with dst=Ref placeholder (unused); then return
            var dummyDst = new TypedReg(arity + 3, IrType.Void);
            instrs.Add(new VCallInstr(dummyDst, recvReg, methodName, argRegs));
            term = new RetTerm(null);
            maxReg = arity + 4;
        }
        else
        {
            var retIr  = FunctionEmitter.ToIrType(signature.Ret);
            var retReg = new TypedReg(arity + 3, retIr);
            instrs.Add(new VCallInstr(retReg, recvReg, methodName, argRegs));
            term = new RetTerm(retReg);
            maxReg = arity + 4;
        }

        var block = new IrBlock("entry", instrs, term);
        var retName = signature.Ret == Z42Type.Void
            ? "void"
            : signature.Ret.ToString() ?? "object";

        var thunk = new IrFunction(
            Name: thunkName,
            ParamCount: 1 + arity,        // env + user args
            RetType: retName,
            ExecMode: "Interp",
            Blocks: new List<IrBlock> { block },
            ExceptionTable: null,
            IsStatic: true,
            MaxReg: maxReg,
            LineTable: null,
            LocalVarTable: null);

        _liftedFunctions.Add(thunk);
        _instanceMethodThunks[cacheKey] = thunkName;
        return thunkName;
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
}
