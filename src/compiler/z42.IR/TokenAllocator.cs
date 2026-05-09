namespace Z42.IR;

/// <summary>
/// Phase 3 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09): assigns deterministic
/// <see cref="MethodId"/> / <see cref="TypeId"/> / <see cref="StaticFieldId"/>
/// to every intra-module declaration, and routes cross-zpkg references to a
/// sorted import_table indexed off <c>IMPORT_BASE</c>.
///
/// <para>Determinism is the core invariant — same source + same toolchain →
/// byte-identical output. Hence:</para>
/// <list type="bullet">
///   <item>Intra-module IDs are assigned in <see cref="StringComparer.Ordinal"/> order.</item>
///   <item>Import table entries are sorted by <c>(kind, name)</c> before assignment.</item>
///   <item>No reliance on hash table iteration order anywhere in the pipeline.</item>
/// </list>
///
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item>Construct.</item>
///   <item>Call <c>RegisterClass</c> / <c>RegisterMethod</c> / <c>RegisterStaticField</c>
///         for every intra-module declaration (any order).</item>
///   <item>Call <c>DiscoverImport</c> for every cross-zpkg reference seen during
///         instruction emit (any order).</item>
///   <item>Call <see cref="Build"/> to finalize. After this point, the
///         <c>Resolve*</c> APIs and <see cref="ImportTable"/> are valid.</item>
///   <item>Call <c>ResolveMethod</c> / <c>ResolveType</c> / <c>ResolveStaticField</c>
///         to get tokens for emit.</item>
/// </list>
///
/// <para>Note: <c>BuiltinInstr.Name</c>, <c>VCallInstr.Method</c>, and
/// <c>FieldGet/Set.FieldName</c> are NOT in scope for this allocator —
/// builtins are a closed set resolved by the runtime <c>BUILTINS</c> table at
/// load time, and method/field references on virtual call / field-get sites
/// are receiver-type-dependent (resolved through ICs, not allocator).</para>
/// </summary>
public sealed class TokenAllocator
{
    private readonly SortedSet<string> _moduleClasses       = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _moduleMethods       = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _moduleStaticFields  = new(StringComparer.Ordinal);
    private readonly SortedSet<(ImportKind Kind, string Name)> _imports = new(ImportTupleComparer.Instance);

    private Dictionary<string, TypeId>?         _typeMap;
    private Dictionary<string, MethodId>?       _methodMap;
    private Dictionary<string, StaticFieldId>?  _staticFieldMap;
    private Dictionary<(ImportKind, string), uint>? _importIdxMap;
    private List<ImportEntry>?                  _importTable;

    private bool _built;

    /// <summary>Build a finalized <see cref="TokenAllocator"/> from a complete
    /// <see cref="IrModule"/>. Walks the module's declarations + every
    /// instruction's string refs to register intra-module IDs and discover
    /// cross-zpkg imports, then calls <see cref="Build"/>.
    ///
    /// <para>This is the single source of truth for the allocator-building
    /// algorithm — both <c>IrGen</c> (compile-time, sibling output) and
    /// <c>ZbcWriter</c> (encode-time, when caller didn't thread allocator)
    /// invoke this so determinism is preserved across all entry points.</para>
    /// </summary>
    public static TokenAllocator FromModule(IrModule module)
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

        // ── Pass 2: scan instructions for refs ────────────────────────────
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
                // BuiltinInstr.Name / VCallInstr.Method / Field*.FieldName /
                // CallNative*  → not in allocator (closed set / receiver-type-
                // dependent / native interop).
            }
        }

        allocator.Build();
        return allocator;
    }

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

    private static void RegisterStaticFieldRef(
        TokenAllocator allocator, HashSet<string> localClassNames, string fieldFqName)
    {
        var dot = fieldFqName.LastIndexOf('.');
        if (dot < 0)
        {
            allocator.RegisterStaticField(fieldFqName);
            return;
        }
        var ownerClass = fieldFqName[..dot];
        if (localClassNames.Contains(ownerClass))
            allocator.RegisterStaticField(fieldFqName);
        else
            allocator.DiscoverImport(ImportKind.StaticField, fieldFqName);
    }

    /// <summary>Register an intra-module class declaration. Idempotent — duplicate
    /// registrations are silently ignored.</summary>
    public void RegisterClass(string fqName)
    {
        EnsureNotBuilt();
        _moduleClasses.Add(fqName);
    }

    /// <summary>Register an intra-module method declaration. FQ name should
    /// include any arity-overload suffix (<c>$N</c>) the compiler appends.</summary>
    public void RegisterMethod(string fqName)
    {
        EnsureNotBuilt();
        _moduleMethods.Add(fqName);
    }

    /// <summary>Register an intra-module static field. <c>fqName</c> is the
    /// fully-qualified <c>{class}.{field}</c> form used in StaticGet/Set.</summary>
    public void RegisterStaticField(string fqName)
    {
        EnsureNotBuilt();
        _moduleStaticFields.Add(fqName);
    }

    /// <summary>Discover a cross-zpkg reference. Idempotent — duplicate (kind, name)
    /// pairs are deduplicated. Order of registration does not affect final
    /// import_table indices (sorted at <see cref="Build"/>).</summary>
    public void DiscoverImport(ImportKind kind, string name)
    {
        EnsureNotBuilt();
        _imports.Add((kind, name));
    }

    /// <summary>Sort all registered declarations and assign IDs. After this,
    /// no further registration is allowed and <c>Resolve*</c> / <see cref="ImportTable"/>
    /// can be queried.</summary>
    public void Build()
    {
        if (_built) return;

        // Intra-module sets are already sorted (SortedSet); enumerate to assign IDs.
        _typeMap = new Dictionary<string, TypeId>(_moduleClasses.Count, StringComparer.Ordinal);
        uint typeNext = 0;
        foreach (var name in _moduleClasses)
            _typeMap[name] = new TypeId(typeNext++);

        _methodMap = new Dictionary<string, MethodId>(_moduleMethods.Count, StringComparer.Ordinal);
        uint methodNext = 0;
        foreach (var name in _moduleMethods)
            _methodMap[name] = new MethodId(methodNext++);

        _staticFieldMap = new Dictionary<string, StaticFieldId>(_moduleStaticFields.Count, StringComparer.Ordinal);
        uint sfNext = 0;
        foreach (var name in _moduleStaticFields)
            _staticFieldMap[name] = new StaticFieldId(sfNext++);

        // Sort import set + assign sequential indices (SortedSet enumerates in order).
        _importTable = new List<ImportEntry>(_imports.Count);
        _importIdxMap = new Dictionary<(ImportKind, string), uint>(_imports.Count);
        uint importIdx = 0;
        foreach (var (kind, name) in _imports)
        {
            _importTable.Add(new ImportEntry(kind, name));
            _importIdxMap[(kind, name)] = importIdx++;
        }

        _built = true;
    }

    /// <summary>Resolve a method name to its token. Intra-module decls return
    /// a small ID; cross-zpkg refs return <c>ImportBase + import_idx</c>.
    /// Throws if the name was not registered or discovered before <see cref="Build"/>.</summary>
    public MethodId ResolveMethod(string fqName)
    {
        EnsureBuilt();
        if (_methodMap!.TryGetValue(fqName, out var id))
            return id;
        if (TryResolveImport(ImportKind.Method, fqName, out var importToken))
            return new MethodId(importToken);
        throw new InvalidOperationException(
            $"TokenAllocator: method `{fqName}` was not registered as intra-module nor discovered as import");
    }

    /// <summary>Resolve a class / type name to its token.</summary>
    public TypeId ResolveType(string fqName)
    {
        EnsureBuilt();
        if (_typeMap!.TryGetValue(fqName, out var id))
            return id;
        if (TryResolveImport(ImportKind.Type, fqName, out var importToken))
            return new TypeId(importToken);
        throw new InvalidOperationException(
            $"TokenAllocator: type `{fqName}` was not registered as intra-module nor discovered as import");
    }

    /// <summary>Resolve a static field name to its token.</summary>
    public StaticFieldId ResolveStaticField(string fqName)
    {
        EnsureBuilt();
        if (_staticFieldMap!.TryGetValue(fqName, out var id))
            return id;
        if (TryResolveImport(ImportKind.StaticField, fqName, out var importToken))
            return new StaticFieldId(importToken);
        throw new InvalidOperationException(
            $"TokenAllocator: static field `{fqName}` was not registered as intra-module nor discovered as import");
    }

    /// <summary>Sorted import_table for ZbcWriter to emit. Order: by ImportKind
    /// byte tag, then by name (Ordinal).</summary>
    public IReadOnlyList<ImportEntry> ImportTable
    {
        get
        {
            EnsureBuilt();
            return _importTable!;
        }
    }

    private bool TryResolveImport(ImportKind kind, string name, out uint token)
    {
        if (_importIdxMap!.TryGetValue((kind, name), out var idx))
        {
            token = TokenConsts.ImportBase + idx;
            return true;
        }
        token = 0;
        return false;
    }

    private void EnsureNotBuilt()
    {
        if (_built)
            throw new InvalidOperationException("TokenAllocator: cannot register after Build()");
    }

    private void EnsureBuilt()
    {
        if (!_built)
            throw new InvalidOperationException("TokenAllocator: must call Build() before resolving");
    }

    private sealed class ImportTupleComparer : IComparer<(ImportKind Kind, string Name)>
    {
        public static readonly ImportTupleComparer Instance = new();
        public int Compare((ImportKind Kind, string Name) a, (ImportKind Kind, string Name) b)
        {
            int byKind = ((byte)a.Kind).CompareTo((byte)b.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Name, b.Name);
        }
    }
}

/// <summary>One entry in the zbc IMPT section: (kind tag, FQ name).</summary>
public sealed record ImportEntry(ImportKind Kind, string Name);
