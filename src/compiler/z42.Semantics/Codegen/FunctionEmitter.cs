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
    /// See docs/design/closure.md §3.4 + impl-local-fn-l2 design Decision 7.
    private readonly Dictionary<string, string> _localFnLiftedNames = new();

    /// Register holding this function's `env` (Vec<Value>) when emitting a
    /// capturing lifted body — i.e. the closure's heap-allocated env passed
    /// as the first implicit parameter. -1 means the current emitter scope is
    /// not a capturing-lifted body (so `BoundCapturedIdent` is unreachable).
    /// See docs/design/closure.md §6 + impl-closure-l3-core design Decision 7.
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

    internal IrFunction EmitMethod(
        string className, FunctionDecl method, BoundBlock body, string methodIrName)
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

        // Emit base constructor call at the start of derived constructors
        bool isCtor = !isStatic && method.Name == className;
        if (isCtor && method.BaseCtorArgs is { }
            && _ctx.ClassRegistry.TryGetBaseClassName(_ctx.QualifyName(className), out var baseQual)
            && baseQual is not null
            && _ctx.SemanticModel.BoundBaseCtorArgs.TryGetValue(method, out var boundBaseArgs))
        {
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

        EmitBoundBlock(body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retType = isCtor ? "void" : TypeName(method.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        var lineTable = _lineTable.Count > 0 ? _lineTable : null;
        var localVars = SnapshotLocalVarTable();
        int paramCount = method.Params.Count + paramOffset;
        // L3-G3a: method constraint lookup uses `{ClassName}.{MethodName}` key
        var constraints = IrGen.BuildConstraintList(
            $"{className}.{method.Name}", method.TypeParams, _ctx.SemanticModel?.FuncConstraints);
        return new IrFunction(methodIrName, paramCount, retType, "Interp", _blocks, excTable,
            IsStatic: isStatic, MaxReg: _nextReg, LineTable: lineTable, LocalVarTable: localVars,
            TypeParams: method.TypeParams,
            TypeParamConstraints: constraints);
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
        if (cls.Methods.TryGetValue(baseSimpleName, out var single) && ArityMatches(single))
            return baseSimpleName;

        // 重载
        foreach (var (key, sig) in cls.Methods)
        {
            if (key.StartsWith(baseSimpleName + "$") && ArityMatches(sig))
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
        return new IrFunction(_ctx.QualifyName(fn.Name), fn.Params.Count, retType,
            "Interp", _blocks, excTable, MaxReg: _nextReg, LineTable: lineTable, LocalVarTable: localVars,
            TypeParams: fn.TypeParams,
            TypeParamConstraints: constraints);
    }

    internal IrFunction EmitStaticInit(CompilationUnit cu)
    {
        _currentClassName = null;
        StartBlock("entry");

        // Topologically sort classes by static field initialization dependencies.
        // If class A's static initializer references class B's static field,
        // B must be initialized before A.
        var sortedClasses = TopologicalSortStaticInits(cu);

        foreach (var cls in sortedClasses)
        {
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                string key = $"{_ctx.QualifyName(cls.Name)}.{field.Name}";
                TypedReg valReg;
                if (field.Initializer != null)
                {
                    valReg = EmitExpr(_ctx.SemanticModel.BoundStaticInits[field]);
                }
                else
                {
                    valReg = field.Type switch
                    {
                        NamedType { Name: "int" } => Alloc(IrType.I32),
                        NamedType { Name: "long" } => Alloc(IrType.I64),
                        NamedType { Name: "short" } => Alloc(IrType.I16),
                        NamedType { Name: "byte" } => Alloc(IrType.U8),
                        NamedType { Name: "double" } => Alloc(IrType.F64),
                        NamedType { Name: "float" } => Alloc(IrType.F32),
                        NamedType { Name: "bool" } => Alloc(IrType.Bool),
                        _ => Alloc(IrType.Ref),
                    };
                    IrInstr defaultInstr = field.Type switch
                    {
                        NamedType { Name: "int" or "long" or "short" or "byte" }
                            => new ConstI64Instr(valReg, 0),
                        NamedType { Name: "double" or "float" }
                            => new ConstF64Instr(valReg, 0.0),
                        NamedType { Name: "bool" }
                            => new ConstBoolInstr(valReg, false),
                        _ => new ConstNullInstr(valReg),
                    };
                    Emit(defaultInstr);
                }
                Emit(new StaticSetInstr(key, valReg));
            }
        }

        EndBlock(new RetTerm(null));
        string initName = _ctx.QualifyName("__static_init__");
        return new IrFunction(initName, 0, "void", "Interp", _blocks, MaxReg: _nextReg);
    }

    // ── Block management ─────────────────────────────────────────────────────

    private void StartBlock(string label)
    {
        _curLabel   = label;
        _curInstrs  = new List<IrInstr>();
        _blockEnded = false;
    }

    private void EndBlock(IrTerminator term)
    {
        if (_blockEnded) return;
        _blocks.Add(new IrBlock(_curLabel, _curInstrs, term));
        _blockEnded = true;
    }

    private string FreshLabel(string hint) => $"{hint}_{_nextLabelId++}";

    private void Emit(IrInstr instr)
    {
        if (!_blockEnded)
            _curInstrs.Add(instr);
    }

    /// Record a source location before emitting instructions for a node.
    /// Only emits a line table entry when the line number changes (RLE compression).
    private void TrackLine(Core.Text.Span span)
    {
        if (span.Line <= 0 || span.Line == _lastLine) return;
        _lastLine = span.Line;
        int blockIdx = _blocks.Count; // current block = next to be sealed
        int instrIdx = _curInstrs.Count;
        string? file = span.File != _sourceFile ? span.File : null;
        _lineTable.Add(new IrLineEntry(blockIdx, instrIdx, span.Line, file));
    }

    private TypedReg Alloc(IrType type = IrType.Unknown) => new(_nextReg++, type);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        // 2026-04-28 fix-generic-type-roundtrip：保留 generic type-args（之前
        // 落到 "unknown"），让 KeyValuePair<K, V>[] 等返回类型在 IR FUNC.RetType
        // 字段里保持完整名字，下游 TypeChecker 能正确还原 instantiation 关系。
        GenericType gt => $"{gt.Name}<{string.Join(", ", gt.TypeArgs.Select(TypeName))}>",
        _             => "unknown"
    };

    /// Write a new value back to a named variable (now pure register-based).
    private void WriteBackName(string name, TypedReg valReg)
    {
        if (_instanceFields.Contains(name))
        {
            Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), name, valReg));
        }
        else
        {
            // All local variables (mutable or not) now have a register ID
            if (!_locals.TryGetValue(name, out var varReg))
            {
                // First assignment to this variable: allocate a new register
                varReg = new TypedReg(_nextReg++, valReg.Type);
                _locals[name] = varReg;
            }
            // Copy the value to the variable's register
            if (varReg.Id != valReg.Id)
                Emit(new CopyInstr(varReg, valReg));
        }
    }

    // ── Debug: local variable table snapshot ─────────────────────────────────

    private List<IrLocalVarEntry>? SnapshotLocalVarTable()
    {
        if (_locals.Count == 0) return null;
        return _locals
            .Select(kv => new IrLocalVarEntry(kv.Key, kv.Value.Id))
            .OrderBy(e => e.RegId)
            .ToList();
    }

    // ── Z42Type / TypeExpr → IrType mapping ─────────────────────────────────
    // All mappings now come from TypeRegistry (single source of truth).

    /// Maps a Z42 semantic type to an IR type tag.
    internal static IrType ToIrType(Z42Type type) => type switch
    {
        Z42PrimType { Name: var n } => TypeRegistry.GetIrType(n),
        Z42ArrayType or Z42ClassType or Z42OptionType or Z42NullType => IrType.Ref,
        Z42VoidType => IrType.Void,
        _ => IrType.Unknown,
    };

    /// Maps a parser TypeExpr to an IrType (used for parameters where no Z42Type is available).
    internal static IrType ToIrType(TypeExpr typeExpr) => typeExpr switch
    {
        NamedType { Name: var n } => TypeRegistry.GetIrType(n),
        ArrayType or OptionType => IrType.Ref,
        VoidType => IrType.Void,
        _ => IrType.Unknown,
    };

    // ── Static init topological sort ─────────────────────────────────────────

    /// Sort classes by static field initialization dependencies.
    /// If class A's static initializer references class B, B appears before A.
    /// Falls back to declaration order if no dependencies exist.
    private IReadOnlyList<ClassDecl> TopologicalSortStaticInits(CompilationUnit cu)
    {
        var classesWithStatic = cu.Classes
            .Where(c => c.Fields.Any(f => f.IsStatic))
            .ToList();

        if (classesWithStatic.Count <= 1) return classesWithStatic;

        var classNames = new HashSet<string>(classesWithStatic.Select(c => c.Name));

        // Build dependency graph: className → set of classes it depends on
        var deps = new Dictionary<string, HashSet<string>>();
        foreach (var cls in classesWithStatic)
        {
            var clsDeps = new HashSet<string>();
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                if (_ctx.SemanticModel.BoundStaticInits.TryGetValue(field, out var initExpr))
                    CollectClassRefs(initExpr, classNames, cls.Name, clsDeps);
            }
            deps[cls.Name] = clsDeps;
        }

        // Kahn's algorithm for topological sort
        var inDegree = classesWithStatic.ToDictionary(c => c.Name, _ => 0);
        foreach (var (cls, clsDeps) in deps)
            foreach (var dep in clsDeps)
                if (inDegree.ContainsKey(dep))
                    inDegree[cls]++;

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<ClassDecl>();
        var byName = classesWithStatic.ToDictionary(c => c.Name);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            sorted.Add(byName[name]);
            // For each class that depends on `name`, reduce in-degree
            foreach (var (cls, clsDeps) in deps)
            {
                if (clsDeps.Contains(name))
                {
                    inDegree[cls]--;
                    if (inDegree[cls] == 0)
                        queue.Enqueue(cls);
                }
            }
        }

        // If cycle detected, fall back to declaration order (cycle is a user bug,
        // but we don't want to crash the compiler — the runtime will handle it)
        return sorted.Count == classesWithStatic.Count ? sorted : classesWithStatic;
    }

    /// Recursively scan a BoundExpr for references to other classes' static members.
    private static void CollectClassRefs(
        BoundExpr expr, HashSet<string> classNames, string self, HashSet<string> refs)
    {
        switch (expr)
        {
            case BoundMember m:
                // member access: if target resolves to a class with static fields, record dep
                if (m.Target is BoundIdent id && classNames.Contains(id.Name) && id.Name != self)
                    refs.Add(id.Name);
                CollectClassRefs(m.Target, classNames, self, refs);
                break;
            case BoundCall c:
                if (c.Receiver != null) CollectClassRefs(c.Receiver, classNames, self, refs);
                foreach (var a in c.Args) CollectClassRefs(a, classNames, self, refs);
                break;
            case BoundBinary b:
                CollectClassRefs(b.Left, classNames, self, refs);
                CollectClassRefs(b.Right, classNames, self, refs);
                break;
            case BoundUnary u:
                CollectClassRefs(u.Operand, classNames, self, refs);
                break;
            case BoundConditional cond:
                CollectClassRefs(cond.Cond, classNames, self, refs);
                CollectClassRefs(cond.Then, classNames, self, refs);
                CollectClassRefs(cond.Else, classNames, self, refs);
                break;
            case BoundCast cast:
                CollectClassRefs(cast.Operand, classNames, self, refs);
                break;
            case BoundInterpolatedStr interp:
                foreach (var part in interp.Parts)
                    if (part is BoundExprPart ep) CollectClassRefs(ep.Inner, classNames, self, refs);
                break;
        }
    }
}
