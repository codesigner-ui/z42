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

    // ── Debug line tracking ──────────────────────────────────────────────────
    private List<IrLineEntry> _lineTable = new();
    private int _lastLine = -1;
    private string? _sourceFile;

    internal FunctionEmitter(IEmitterContext ctx) => _ctx = ctx;

    // ── Entry points ─────────────────────────────────────────────────────────

    internal IrFunction EmitMethod(
        string className, FunctionDecl method, BoundBlock body, string methodIrName)
    {
        bool isStatic = method.IsStatic;
        _currentClassName = className;
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
            var baseCtorIrName = $"{baseQual}.{baseSimpleName}";
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

    internal IrFunction EmitFunction(FunctionDecl fn, BoundBlock body)
    {
        _currentClassName = null;
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
