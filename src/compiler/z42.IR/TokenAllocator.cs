namespace Z42.IR;

/// <summary>
/// Phase 3 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09 redesigned): assigns
/// per-module token IDs to functions and classes, and resolves cross-zpkg
/// references through the STRS string pool.
///
/// <para><b>Wire format encoding</b> (matches Rust <c>IdMap</c>):</para>
/// <code>
///   intra-module:    [0,             0x7FFF_FFFE]   token = local index
///   IMPORT_BASE:     0x8000_0000
///   cross-zpkg:      [0x8000_0000,   0xFFFF_FFFE]   token - IMPORT_BASE = STRS idx
///   UNRESOLVED:      0xFFFF_FFFF
/// </code>
///
/// <para><b>Determinism</b>: same source + same toolchain → byte-identical zbc.
/// Local IDs come from <c>module.Functions</c> / <c>module.Classes</c> insertion
/// order (which is source order from IrGen). STRS pool ordering is by intern
/// order (also source order). No HashMap iteration in the emit path.</para>
///
/// <para>The 2026-05-09 redesign replaced the original Ordinal-sort + IMPT-extension
/// approach (see commit <c>833193a</c> on <c>wip/phase3-s3-broken</c> for the
/// abandoned attempt). New form keeps wire format simple by reusing STRS for
/// cross-zpkg refs and using insertion-order indices for intra-module refs.</para>
///
/// <para><b>Not in scope</b>: <c>BuiltinInstr.Name</c>, <c>VCallInstr.Method</c>,
/// <c>FieldGet/Set.FieldName</c>, <c>CallNative*.{Module,TypeName,Symbol}</c>
/// — these are not tokenized in v1.0 (BUILTINS is closed-set runtime resolved;
/// VCall/Field are receiver-type-dependent IC paths; native interop is a
/// separate concern). Their wire encoding remains STRS pool index.</para>
/// </summary>
public sealed class TokenAllocator
{
    private readonly Dictionary<string, uint> _localFunctions;
    private readonly Dictionary<string, uint> _localClasses;

    private TokenAllocator(Dictionary<string, uint> funcs, Dictionary<string, uint> classes)
    {
        _localFunctions = funcs;
        _localClasses   = classes;
    }

    /// <summary>Build a finalized <see cref="TokenAllocator"/> from a complete
    /// <see cref="IrModule"/>. Local IDs are <c>module.Functions[i].Name → i</c>
    /// and <c>module.Classes[i].Name → i</c>.</summary>
    public static TokenAllocator FromModule(IrModule module)
    {
        var funcs = new Dictionary<string, uint>(module.Functions.Count, StringComparer.Ordinal);
        for (int i = 0; i < module.Functions.Count; i++)
        {
            // Same name appearing twice would be a compiler bug; first registration wins.
            funcs.TryAdd(module.Functions[i].Name, (uint)i);
        }
        var classes = new Dictionary<string, uint>(module.Classes.Count, StringComparer.Ordinal);
        for (int i = 0; i < module.Classes.Count; i++)
        {
            classes.TryAdd(module.Classes[i].Name, (uint)i);
        }
        return new TokenAllocator(funcs, classes);
    }

    /// <summary>Resolve a method reference. Local methods return their
    /// <c>module.Functions</c> index; otherwise the name is interned in the
    /// <paramref name="pool"/> and <c>IMPORT_BASE + idx</c> is returned.</summary>
    public uint ResolveMethod(string fqName, BinaryFormat.StringPool pool)
    {
        if (_localFunctions.TryGetValue(fqName, out var local))
            return local;
        return TokenConsts.ImportBase + (uint)pool.Intern(fqName);
    }

    /// <summary>Resolve a type reference. Same encoding as <see cref="ResolveMethod"/>.</summary>
    public uint ResolveType(string fqName, BinaryFormat.StringPool pool)
    {
        if (_localClasses.TryGetValue(fqName, out var local))
            return local;
        return TokenConsts.ImportBase + (uint)pool.Intern(fqName);
    }
}

