namespace Z42.IR;

/// An entry in the dependency call index.
/// QualifiedName  — fully qualified function name in the dependency IR (e.g. "Std.IO.Console.WriteLine")
/// Namespace      — dependency namespace that owns this function (e.g. "Std.IO")
public sealed record DepCallEntry(string QualifiedName, string Namespace);

/// Index that maps short call-site names → dependency qualified function names.
///
/// Populated by BuildCommand from pre-built stdlib .zpkg files; consumed by IrGen
/// to emit CallInstr instead of BuiltinInstr when a call site resolves to a dependency function.
///
/// Two lookup paths:
///   • Static:   "ClassName.MethodName"  — for  Console.WriteLine(...), Math.Abs(...)
///   • Instance: "MethodName"            — for  str.Substring(...),  str.ToLower()
///               "MethodName$<arity>"    — arity-qualified variant for overloads
///
/// When a method name is ambiguous across multiple dependency classes (e.g. "Contains" in
/// both String and some other class), it is omitted from the instance index and the
/// caller falls through to VCallInstr runtime dispatch.
public sealed class DependencyIndex
{
    private readonly IReadOnlyDictionary<string, DepCallEntry> _staticIndex;
    private readonly IReadOnlyDictionary<string, DepCallEntry> _instanceIndex;

    private DependencyIndex(
        IReadOnlyDictionary<string, DepCallEntry> staticIndex,
        IReadOnlyDictionary<string, DepCallEntry> instanceIndex)
    {
        _staticIndex   = staticIndex;
        _instanceIndex = instanceIndex;
    }

    // ── Lookups ────────────────────────────────────────────────────────────────

    /// Try to find a static dependency call for "ClassName.MethodName" (user writes Foo.Bar(...)).
    public bool TryGetStatic(string cls, string method, out DepCallEntry entry) =>
        _staticIndex.TryGetValue($"{cls}.{method}", out entry!);

    /// Try to find an instance dependency call for "MethodName" with the given user argument count
    /// (i.e. excluding the implicit receiver).  Also tries the bare "MethodName" key.
    public bool TryGetInstance(string method, int userArgCount, out DepCallEntry entry)
    {
        // Try arity-qualified key first, then bare name.
        return _instanceIndex.TryGetValue($"{method}${userArgCount}", out entry!)
            || _instanceIndex.TryGetValue(method, out entry!);
    }

    // ── Builder ────────────────────────────────────────────────────────────────

    /// Build an index from a collection of dependency IrModules.
    ///
    /// Each module's Name is used as the namespace prefix (e.g. "Std.IO").
    /// Functions whose names do not start with that prefix are skipped.
    /// Functions named "__static_init__" are skipped.
    /// The function name format is: namespace.ClassName.MethodName[[$arity]]
    public static DependencyIndex Build(IEnumerable<(IrModule Module, string Namespace)> depModules)
    {
        var staticBuf   = new Dictionary<string, DepCallEntry>(StringComparer.Ordinal);
        // Track which instance keys are ambiguous (seen from >1 class).
        var instanceBuf = new Dictionary<string, DepCallEntry>(StringComparer.Ordinal);
        var ambiguous   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (module, ns) in depModules)
        {
            foreach (var fn in module.Functions)
            {
                string name = fn.Name;

                // Skip VM-internal helpers.
                if (name.EndsWith("__static_init__", StringComparison.Ordinal)) continue;
                if (!name.StartsWith(ns + ".", StringComparison.Ordinal))       continue;

                // Strip namespace prefix: "Std.IO.Console.WriteLine" → "Console.WriteLine"
                string withoutNs = name[(ns.Length + 1)..]; // "Console.WriteLine"

                int dot = withoutNs.IndexOf('.');
                if (dot < 0) continue; // no class prefix — skip

                string shortClass  = withoutNs[..dot];         // "Console"
                string methodPart  = withoutNs[(dot + 1)..];   // "WriteLine"  or  "Substring$1"

                var entry = new DepCallEntry(name, ns);

                // ── Static index ───────────────────────────────────────────────
                // Key: "ClassName.MethodName"  (with any $arity suffix)
                string staticKey = $"{shortClass}.{methodPart}";
                staticBuf.TryAdd(staticKey, entry);

                // Also add a bare (no-arity) static key when the method has an arity suffix.
                if (methodPart.Contains('$'))
                {
                    string bareStaticKey = $"{shortClass}.{methodPart[..methodPart.IndexOf('$')]}";
                    staticBuf.TryAdd(bareStaticKey, entry); // first arity wins for bare key
                }

                // ── Instance index ─────────────────────────────────────────────
                // Static methods (is_static=true) are never called as instance methods:
                // skip them here so they don't cause false ambiguity with instance methods
                // of the same name (e.g. Assert.Contains vs String.Contains).
                if (fn.IsStatic) continue;

                // Object virtual methods are excluded from instance index (see below).
                if (shortClass == "Object") continue;

                // Key: "MethodName$<userArity>"  where userArity = paramCount - 1.
                // If the function name already encodes arity (e.g. "Substring$1" from
                // compiler-generated overload IR), use it directly — don't append again.
                int userArity = fn.ParamCount - 1; // subtract implicit 'this'
                if (userArity < 0) userArity = 0;

                string bareMethod = methodPart.Contains('$')
                    ? methodPart[..methodPart.IndexOf('$')]
                    : methodPart;

                // Virtual protocol methods (ToString, Equals, GetHashCode, GetType) must
                // always be dispatched via VCallInstr so user overrides work correctly.
                // This applies to ALL stdlib classes, not just Object — e.g.,
                // StringBuilder.ToString must also go via VCallInstr.
                if (bareMethod is "ToString" or "Equals" or "GetHashCode" or "GetType") continue;
                // Already arity-encoded ("Substring$1") → use as-is; bare name → append userArity.
                string arityKey = methodPart.Contains('$')
                    ? methodPart
                    : $"{methodPart}${userArity}";
                RegisterInstance(instanceBuf, ambiguous, arityKey, entry);

                RegisterInstance(instanceBuf, ambiguous, bareMethod, entry);
            }
        }

        // Remove all ambiguous keys.
        foreach (var key in ambiguous)
            instanceBuf.Remove(key);

        return new DependencyIndex(staticBuf, instanceBuf);
    }

    private static void RegisterInstance(
        Dictionary<string, DepCallEntry> buf,
        HashSet<string>                  ambiguous,
        string                           key,
        DepCallEntry                     entry)
    {
        if (ambiguous.Contains(key)) return;

        if (buf.TryGetValue(key, out var existing))
        {
            // Different class → ambiguous; same class (e.g. arity-free vs arity-specific) → ok.
            string existingClass = ExtrClass(existing.QualifiedName);
            string newClass      = ExtrClass(entry.QualifiedName);
            if (existingClass != newClass)
                ambiguous.Add(key);
        }
        else
        {
            buf[key] = entry;
        }
    }

    private static string ExtrClass(string qualifiedName)
    {
        // "Std.IO.Console.WriteLine" → "Console"
        int last   = qualifiedName.LastIndexOf('.');
        if (last < 0) return qualifiedName;
        int second = qualifiedName.LastIndexOf('.', last - 1);
        return second < 0 ? qualifiedName[..last] : qualifiedName[(second + 1)..last];
    }

    // ── Empty sentinel ─────────────────────────────────────────────────────────

    public static DependencyIndex Empty { get; } =
        new(new Dictionary<string, DepCallEntry>(),
            new Dictionary<string, DepCallEntry>());
}
