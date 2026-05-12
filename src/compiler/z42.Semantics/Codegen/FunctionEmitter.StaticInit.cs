using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Static initializer emission + class-ref dependency analysis.
///
/// spec split-function-emitter (2026-05-12): extracted from FunctionEmitter.cs
/// to keep the main file under the 500 LOC hard limit. Zero behavior change —
/// `EmitStaticInit` / `TopologicalSortStaticInits` / `CollectClassRefs` /
/// nested `ClassRefScanner` were moved verbatim from the monolith.
internal sealed partial class FunctionEmitter
{
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

    /// Recursively scan a BoundExpr for references to other classes' static
    /// members. Used to build the static-init dependency graph for topological
    /// sort. introduce-bound-visitor (2026-05-10): migrated from manual switch
    /// to a `ClassRefScanner` walker subclass — preserves the original (partial)
    /// node coverage exactly so the dep graph is byte-identical.
    private static void CollectClassRefs(
        BoundExpr expr, HashSet<string> classNames, string self, HashSet<string> refs)
    {
        new ClassRefScanner(classNames, self, refs).Visit(expr);
    }

    /// Walker that records other-class static refs encountered through
    /// `BoundMember(BoundIdent)`. Coverage matches the legacy switch: only
    /// Member / Call / Binary / Unary / Conditional / Cast / InterpolatedStr
    /// recurse into children; other interior nodes (Lambda / New / ArrayCreate /
    /// ArrayLit / Index / Assign / etc.) intentionally do not, mirroring the
    /// pre-refactor behavior.
    private sealed class ClassRefScanner : BoundExprWalker
    {
        private readonly HashSet<string> _classNames;
        private readonly string _self;
        private readonly HashSet<string> _refs;

        public ClassRefScanner(HashSet<string> classNames, string self, HashSet<string> refs)
        {
            _classNames = classNames;
            _self = self;
            _refs = refs;
        }

        // ── Recurse cases (mirror legacy switch) ──────────────────────────────
        protected override Unit VisitMember(BoundMember m)
        {
            if (m.Target is BoundIdent id && _classNames.Contains(id.Name) && id.Name != _self)
                _refs.Add(id.Name);
            return base.VisitMember(m);
        }

        // VisitCall, VisitBinary, VisitUnary, VisitConditional, VisitCast,
        // VisitInterpolatedStr inherit Walker's default (recurse into children).

        // ── Block cases (legacy switch ignored these — preserve exactly) ──────
        protected override Unit VisitAssign(BoundAssign a)             => default;
        protected override Unit VisitPostfix(BoundPostfix p)           => default;
        protected override Unit VisitLambda(BoundLambda l)             => default;
        protected override Unit VisitIndirectCall(BoundIndirectCall ic) => default;
        protected override Unit VisitModifiedArg(BoundModifiedArg m)   => default;
        protected override Unit VisitIndex(BoundIndex i)               => default;
        protected override Unit VisitNew(BoundNew n)                   => default;
        protected override Unit VisitArrayCreate(BoundArrayCreate ac)  => default;
        protected override Unit VisitArrayLit(BoundArrayLit al)        => default;
        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc)         => default;
        protected override Unit VisitNullConditional(BoundNullConditional nc)   => default;
        protected override Unit VisitIsPattern(BoundIsPattern ip)      => default;
        protected override Unit VisitSwitchExpr(BoundSwitchExpr s)     => default;
    }
}
