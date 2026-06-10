using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public sealed partial class SymbolCollector
{
    /// True if the class should NOT implicitly inherit from Object.
    private static bool ExcludeFromImplicitObject(ClassDecl cls) =>
        cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name);

    /// 2026-05-07 add-class-arity-overloading: arity-aware class registry lookup.
    /// For a generic class (`cls.TypeParams != null`), tries `Name$N` key first
    /// (shadow slot used when there's a non-generic same-name sibling) then
    /// falls back to bare `Name` (default slot when no collision exists).
    /// For non-generic classes, just bare `Name`.
    /// Use this everywhere `_classes.TryGetValue(cls.Name, ...)` was used to
    /// look up the entry corresponding to `cls`.
    private bool TryGetClassFor(ClassDecl cls, out Z42ClassType ct)
    {
        int arity = cls.TypeParams?.Count ?? 0;
        if (arity > 0 && _classes.TryGetValue($"{cls.Name}${arity}", out var manglee))
        {
            ct = manglee;
            return true;
        }
        return _classes.TryGetValue(cls.Name, out ct!);
    }

    /// Compute the registry key for an *already-registered* class declaration:
    /// returns `Name$N` if the registered Z42ClassType has `HasArityMangle = true`,
    /// else bare `Name`. Use after the pre-pass for downstream lookups / writes.
    private string KeyFor(ClassDecl cls)
    {
        int arity = cls.TypeParams?.Count ?? 0;
        if (arity > 0 && _classes.TryGetValue($"{cls.Name}${arity}", out var ct) && ct.HasArityMangle)
            return $"{cls.Name}${arity}";
        return cls.Name;
    }

    /// Extract short name from an interface-position `TypeExpr` fallback.
    private static string IfaceNameFromTypeExpr(TypeExpr t) => t switch
    {
        NamedType nt   => nt.Name,
        GenericType gt => gt.Name,
        _              => "<unknown>",
    };

    private void CollectClasses(CompilationUnit cu)
    {
        // Pre-register Object's virtual methods and class shape
        bool cuDefinesObject = cu.Classes.Any(c => WellKnownNames.IsObjectClass(c.Name));
        _virtualMethods["Object"] = ["ToString", "Equals", "GetHashCode"];
        if (!cuDefinesObject)
        {
            // 2026-05-07 fix-gettype-on-object-typed-receiver: include `GetType()`
            // in the stub so `Object o = ...; o.GetType()` resolves. The real
            // `Std.Object` (loaded later via MergeImported) declares GetType too;
            // before this fix `MergeImported` used TryAdd which left this stub
            // shadowing the real class, so any GetType call on an Object-typed
            // receiver hit E0402 "type Object has no method GetType".
            // fix-chained-property-dispatch (2026-06-10): GetType's return type
            // resolves to the real `Std.Type` class when it is already present in
            // `_classes` (MergeImported runs before CollectClasses, so the imported
            // Type class — keyed by short name "Type" — is available here). Only
            // falls back to `Z42Type.Unknown` when Type isn't imported (e.g. a CU
            // with no z42.core dependency). Returning Unknown caused chained
            // getter dispatch (`obj.GetType().BaseType.Name`) to degrade: the
            // Unknown intermediate made `.BaseType` / `.Name` hit the silent
            // member fallback (TypeChecker.Exprs.Members.cs:174) → emitted as a
            // FieldGet → runtime "FieldGet on Null". The local-variable form
            // (`Type t = obj.GetType(); t.BaseType.Name`) worked only because the
            // explicit annotation re-resolved the receiver to the real Type class.
            // This is the cross-phase downgrade fixup pattern (philosophy.md):
            // produce the correct type at the source instead of patching consumers.
            // split-symbol-from-type: Object's built-in methods are constructed as
            // MethodSymbol-wrapped IMethodSymbol entries (decl=null, ContainingType
            // assigned post-construction below).
            Z42Type getTypeRet = _classes.TryGetValue("Type", out var typeCls)
                ? typeCls
                : Z42Type.Unknown;
            var objectClassSkeleton = new Z42ClassType(
                "Object",
                new Dictionary<string, IFieldSymbol>(),
                new Dictionary<string, IMethodSymbol>(),
                new Dictionary<string, IFieldSymbol>(),
                new Dictionary<string, IMethodSymbol>(),
                new Dictionary<string, Visibility>(),
                null);
            var objectMethods = new Dictionary<string, IMethodSymbol>
            {
                ["ToString"]    = new MethodSymbol("ToString", objectClassSkeleton,
                                                   new Z42FuncType([], Z42Type.String),
                                                   FunctionModifiers.Virtual, default,
                                                   Visibility.Public, decl: null, testAttributes: null),
                ["Equals"]      = new MethodSymbol("Equals", objectClassSkeleton,
                                                   new Z42FuncType([Z42Type.Object], Z42Type.Bool),
                                                   FunctionModifiers.Virtual, default,
                                                   Visibility.Public, decl: null, testAttributes: null),
                ["GetHashCode"] = new MethodSymbol("GetHashCode", objectClassSkeleton,
                                                   new Z42FuncType([], Z42Type.Int),
                                                   FunctionModifiers.Virtual, default,
                                                   Visibility.Public, decl: null, testAttributes: null),
                ["GetType"]     = new MethodSymbol("GetType", objectClassSkeleton,
                                                   new Z42FuncType([], getTypeRet),
                                                   FunctionModifiers.None, default,
                                                   Visibility.Public, decl: null, testAttributes: null),
            };
            // Rebuild Object with populated methods dict; ContainingType already
            // refers to skeleton (same Name → Equals returns true; consumers that
            // re-query Methods get the populated set via _classes lookup).
            _classes["Object"] = objectClassSkeleton with { Methods = objectMethods };
        }

        // 2026-05-07 add-class-arity-overloading: scan for same-name arity collision.
        // A class needs `HasArityMangle = true` (and registry key `Name$N`) ONLY when
        // a sibling with same source name and a DIFFERENT arity also exists in this CU.
        // Solo generic classes (`class List<T>` with no `class List`) keep bare key,
        // preserving existing IR / VM / zpkg compatibility.
        var groupedByName = cu.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.ToList());
        bool NeedsMangle(ClassDecl cls)
        {
            if (!groupedByName.TryGetValue(cls.Name, out var siblings)) return false;
            if (siblings.Count < 2) return false;
            int myArity = cls.TypeParams?.Count ?? 0;
            // Mangle the generic version(s) when at least one non-generic sibling
            // claims the bare-name slot. Multiple-arity (Foo<T> + Foo<T,U>) without
            // a non-generic sibling is also mangled to disambiguate; non-generic
            // always wins the bare slot.
            if (myArity == 0) return false; // non-generic always claims bare
            return siblings.Any(s => (s.TypeParams?.Count ?? 0) != myArity);
        }
        string ClassKey(ClassDecl cls, bool needsMangle) =>
            needsMangle ? $"{cls.Name}${cls.TypeParams!.Count}" : cls.Name;

        // Pre-pass: register every class name as an empty stub
        foreach (var cls in cu.Classes)
        {
            var effectiveBase = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            var needsMangle = NeedsMangle(cls);
            var key = ClassKey(cls, needsMangle);

            if (_classes.ContainsKey(key))
            {
                // L3-G4d: a local class with the same arity-key as an imported one
                // shadows the import. Drop the imported record silently; duplicate
                // check still fires for local-local conflicts.
                if (_importedClassNames.Remove(key))
                {
                    _importedClassNamespaces.Remove(key);
                    _classes[key] = new Z42ClassType(
                        cls.Name, new Dictionary<string, IFieldSymbol>(),
                        new Dictionary<string, IMethodSymbol>(),
                        new Dictionary<string, IFieldSymbol>(),
                        new Dictionary<string, IMethodSymbol>(),
                        new Dictionary<string, Visibility>(),
                        effectiveBase,
                        TypeParams: cls.TypeParams,
                        HasArityMangle: needsMangle);
                }
                else
                    _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                        $"duplicate class declaration `{cls.Name}`", cls.Span);
            }
            else
                _classes[key] = new Z42ClassType(
                    cls.Name, new Dictionary<string, IFieldSymbol>(),
                    new Dictionary<string, IMethodSymbol>(),
                    new Dictionary<string, IFieldSymbol>(),
                    new Dictionary<string, IMethodSymbol>(),
                    new Dictionary<string, Visibility>(),
                    effectiveBase,
                    TypeParams: cls.TypeParams,
                    HasArityMangle: needsMangle);
        }

        // First pass: collect own fields and methods
        foreach (var cls in cu.Classes)
        {
            if (cls.IsStruct && cls.BaseClass != null)
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"struct `{cls.Name}` cannot inherit from a base class", cls.Span);
            // L3-G4b primitive-as-struct: structs may implement interfaces (C# parity).
            // stdlib declares `struct int : IComparable<int>` etc. for data-driven
            // primitive interface satisfaction.

            // Activate generic type params so T resolves to Z42GenericParamType in fields/methods
            if (cls.TypeParams != null) _activeTypeParams = new HashSet<string>(cls.TypeParams);

            var fields        = new Dictionary<string, IFieldSymbol>();
            var staticFields  = new Dictionary<string, IFieldSymbol>();
            var methods       = new Dictionary<string, IMethodSymbol>();
            var staticMethods = new Dictionary<string, IMethodSymbol>();
            // Track FieldSymbol/MethodSymbol instances so we can fix-up ContainingType
            // after Z42ClassType is constructed (resolves the chicken-and-egg between
            // Z42ClassType.Methods holding IMethodSymbol and IMethodSymbol.ContainingType
            // holding Z42ClassType).
            var symbolsForFixup = new List<IMemberSymbol>();

            foreach (var f in cls.Fields)
            {
                var ft = ResolveType(f.Type);
                var sym = new FieldSymbol(f.Name, containingType: null, ft, f.IsStatic,
                                           f.Span, f.Visibility, isEvent: f.IsEvent, decl: f);
                symbolsForFixup.Add(sym);
                if (f.IsStatic) staticFields[f.Name] = sym;
                else            fields[f.Name]      = sym;
            }
            var methodNameCount = cls.Methods
                .GroupBy(m => (m.Name, m.IsStatic))
                .ToDictionary(g => g.Key, g => g.Count());
            // Spec define-ref-out-in-parameters (Decision 7): same-arity overload
            // by modifier requires a 3rd-axis grouping. When two methods share
            // (Name, IsStatic, Arity) but differ in modifier sequence, fall back
            // to a modifier-tagged key `Name$Arity$<modSig>`.
            var arityGroupCount = cls.Methods
                .GroupBy(m => (m.Name, m.IsStatic, Arity: m.Params.Count))
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var m in cls.Methods)
            {
                // 2026-05-05 fix-method-typeparams: method-level generic params
                // (e.g. `T Foo<T>(T x)` inside a non-generic class) must be
                // activated while resolving this method's signature so `T`
                // becomes Z42GenericParamType rather than the Z42PrimType("T")
                // fallback. Class-level params (line 91) cover generic classes;
                // this branch handles **method-level** params for static and
                // instance methods inside any class (generic or not).
                HashSet<string>? methodTypeParamSnapshot = null;
                if (m.TypeParams is { Count: > 0 } mtp)
                {
                    methodTypeParamSnapshot = _activeTypeParams;
                    _activeTypeParams = methodTypeParamSnapshot is null
                        ? new HashSet<string>(mtp)
                        : new HashSet<string>(methodTypeParamSnapshot.Concat(mtp));
                }

                var retType = m.Name == cls.Name ? (Z42Type)Z42Type.Void : ResolveType(m.ReturnType);
                var sig     = BuildFuncSignature(m.Params, retType);

                if (m.TypeParams is { Count: > 0 })
                    _activeTypeParams = methodTypeParamSnapshot;
                bool isOverloaded     = methodNameCount[(m.Name, m.IsStatic)] > 1;
                bool sameArityCollide = arityGroupCount[(m.Name, m.IsStatic, m.Params.Count)] > 1;
                string regName;
                if (!isOverloaded)
                {
                    regName = m.Name;
                }
                else if (sameArityCollide)
                {
                    // Spec define-ref-out-in-parameters Decision 7: only switch
                    // to modifier-tagged keys when at least one method in the
                    // (Name, IsStatic, Arity) group has a non-None modifier.
                    // Pure same-arity collision (both all-None) keeps legacy
                    // arity-only key so existing same-arity overloads (like
                    // stdlib `Math.Abs(int)` / `Math.Abs(double)`) keep working.
                    bool anyHasModifier = cls.Methods.Any(x =>
                        x.Name == m.Name
                        && x.IsStatic == m.IsStatic
                        && x.Params.Count == m.Params.Count
                        && x.Params.Any(p => p.Modifier != ParamModifier.None));
                    if (anyHasModifier)
                    {
                        var modSig = ModifierMangling.PatternFromParams(m.Params);
                        regName = $"{m.Name}${m.Params.Count}${modSig}";
                    }
                    else
                    {
                        regName = $"{m.Name}${m.Params.Count}";
                    }
                }
                else
                {
                    regName = $"{m.Name}${m.Params.Count}";
                }
                var methodSym = new MethodSymbol(regName, containingType: null, sig,
                                                  m.Modifiers, m.Span, m.Visibility,
                                                  decl: m, testAttributes: m.TestAttributes);
                symbolsForFixup.Add(methodSym);
                if (m.IsStatic) staticMethods[regName] = methodSym;
                else            methods[regName]      = methodSym;
            }

            _activeTypeParams = null;

            var memberVis = new Dictionary<string, Visibility>();
            foreach (var f in cls.Fields)  memberVis[f.Name] = f.Visibility;
            foreach (var m in cls.Methods) memberVis[m.Name] = m.Visibility;

            var effectiveBase2 = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            // 2026-05-03 add-event-keyword-multicast (D2c-多播)：收集 event field
            // 名 → EventFieldNames 集合（用于 BindAssign `+=` / `-=` desugar）。
            HashSet<string>? eventNames = null;
            foreach (var f in cls.Fields)
            {
                if (f.IsEvent)
                {
                    eventNames ??= new HashSet<string>(StringComparer.Ordinal);
                    eventNames.Add(f.Name);
                }
            }
            // 2026-05-07 add-class-arity-overloading: KeyFor(cls) returns the
            // arity-suffixed key when the pre-pass marked this class with
            // HasArityMangle (collision case); bare cls.Name otherwise.
            var clsKey = KeyFor(cls);
            // Preserve HasArityMangle across the `with`-style replacement.
            bool wasMangled = _classes.TryGetValue(clsKey, out var existing) && existing.HasArityMangle;
            var classType = new Z42ClassType(
                cls.Name, fields, methods, staticFields, staticMethods,
                memberVis, effectiveBase2, cls.TypeParams?.AsReadOnly(),
                IsStruct: cls.IsStruct,
                EventFieldNames: eventNames,
                HasArityMangle: wasMangled);
            // Two-phase construction fix-up: assign ContainingType to every
            // FieldSymbol/MethodSymbol now that classType exists. Resolves the
            // chicken-and-egg between Z42ClassType.Methods and IMemberSymbol.ContainingType.
            FixupContainingType(symbolsForFixup, classType);
            _classes[clsKey] = classType;
            // L3-G2.5 chain: resolve each interface TypeExpr to a Z42InterfaceType
            // (with TypeArgs for generic interface references). Failures fall back to
            // name-only stubs to avoid cascading diagnostics.
            if (cls.TypeParams != null) _activeTypeParams = new HashSet<string>(cls.TypeParams);
            var resolvedIfaces = new List<Z42InterfaceType>(cls.Interfaces.Count);
            foreach (var ifaceTy in cls.Interfaces)
            {
                var resolved = ResolveType(ifaceTy);
                if (resolved is Z42InterfaceType it)
                    resolvedIfaces.Add(it);
                else
                    resolvedIfaces.Add(new Z42InterfaceType(
                        IfaceNameFromTypeExpr(ifaceTy),
                        new Dictionary<string, IMethodSymbol>()));
            }
            _activeTypeParams = null;
            _classInterfaces[clsKey] = resolvedIfaces;

            if (cls.IsAbstract) _abstractClasses.Add(clsKey);
            if (cls.IsSealed)   _sealedClasses.Add(clsKey);
            _abstractMethods[clsKey] = cls.Methods
                .Where(m => m.IsAbstract).Select(m => m.Name).ToHashSet();
            _virtualMethods[clsKey] = cls.Methods
                .Where(m => m.IsVirtual || m.IsAbstract).Select(m => m.Name).ToHashSet();
        }

        // Second pass: merge inherited fields/methods
        foreach (var cls in cu.Classes)
        {
            var clsKey = KeyFor(cls);
            var effectiveBase3 = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            if (effectiveBase3 == null) continue;

            if (_sealedClasses.Contains(effectiveBase3))
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"cannot inherit from sealed class `{effectiveBase3}`", cls.Span);

            // fix-memorystream-override-visibility (2026-05-24): defer instance-method
            // override validation to `FinalizeOverrideChecks()`. Running here per-CU
            // missed same-package cross-file base classes whose `_virtualMethods` entry
            // had not yet been populated (alphabetical CU order processes
            // `MemoryStream.z42` before `Stream.z42`). Static override goes through
            // `VerifyStaticOverrides` (interface static members) elsewhere.
            if (cls.Methods.Any(m => m.IsOverride && !m.IsStatic))
                _pendingOverrideChecks.Add((clsKey, cls));

            if (!_classes.TryGetValue(effectiveBase3, out var baseType)) continue;
            var derived = _classes[clsKey];
            var mergedFields  = new Dictionary<string, IFieldSymbol>(baseType.Fields);
            var mergedMethods = new Dictionary<string, IMethodSymbol>(baseType.Methods);
            foreach (var kv in derived.Fields)  mergedFields[kv.Key]  = kv.Value;
            foreach (var kv in derived.Methods) mergedMethods[kv.Key] = kv.Value;
            _classes[clsKey] = derived with { Fields = mergedFields, Methods = mergedMethods };

            var baseAbstract = _abstractMethods.GetValueOrDefault(effectiveBase3, []);
            var ownMethods   = cls.Methods.Select(m => m.Name).ToHashSet();
            var remaining    = baseAbstract.Except(ownMethods).ToHashSet();
            _abstractMethods[clsKey] = [.._abstractMethods.GetValueOrDefault(clsKey, []), ..remaining];
        }

        // Third pass: concrete classes must implement all abstract methods
        foreach (var cls in cu.Classes)
        {
            if (cls.IsAbstract) continue;
            var clsKey = KeyFor(cls);
            if (_abstractMethods.TryGetValue(clsKey, out var unimpl) && unimpl.Count > 0)
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"class `{cls.Name}` must implement abstract method(s): {string.Join(", ", unimpl.Select(m => $"`{m}`"))}",
                    cls.Span);
        }

        // Fourth pass: verify interface implementation completeness
        foreach (var cls in cu.Classes)
        {
            if (cls.IsAbstract) continue;
            var clsKey = KeyFor(cls);
            if (!_classInterfaces.TryGetValue(clsKey, out var ifaces) || ifaces.Count == 0) continue;
            if (!_classes.TryGetValue(clsKey, out var classType)) continue;

            foreach (var ifaceTy in ifaces)
            {
                var ifaceName = ifaceTy.Name;
                if (!_interfaces.TryGetValue(ifaceName, out var iface)) continue;
                foreach (var (methodName, ifaceSym) in iface.Methods)
                {
                    if (!classType.Methods.TryGetValue(methodName, out var implSym))
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` does not implement interface method `{ifaceName}.{methodName}`",
                            cls.Span);
                    else if (implSym.Signature.Params.Count != ifaceSym.Signature.Params.Count)
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` method `{methodName}` has wrong parameter count for interface `{ifaceName}` " +
                            $"(expected {ifaceSym.Signature.Params.Count}, got {implSym.Signature.Params.Count})",
                            cls.Span);
                    else if (!Z42Type.IsAssignableTo(ifaceSym.Signature.Ret, implSym.Signature.Ret))
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` method `{methodName}` return type `{implSym.Signature.Ret}` does not match interface `{ifaceName}` return type `{ifaceSym.Signature.Ret}`",
                            cls.Span);
                }
            }
        }

        // Fifth pass (L3 static abstract interface members): verify `static override`
        // on the class implementer side — spelling防护 + sealed检查 + 漏缺检查.
        VerifyStaticOverrides(cu);
    }

    /// L3 static abstract interface members (C# 11 alignment): check that every
    /// `static override` method on a class targets an interface static member
    /// and obeys its tier rules; check that every abstract interface static
    /// member is overridden by a non-abstract implementer.
    private void VerifyStaticOverrides(CompilationUnit cu)
    {
        foreach (var cls in cu.Classes)
        {
            if (!_classInterfaces.TryGetValue(KeyFor(cls), out var ifaces)) continue;

            // Build combined static-member map from all implemented interfaces.
            // `anyKnownStatic` tracks whether we saw any interface declaration
            // with static members — when every iface is unknown (imported
            // without TSIG info, or sibling file not yet compiled), we skip
            // "no matching override target" diagnostics (can't判断 without data).
            var ifaceStatic = new Dictionary<string, (string Iface, Z42StaticMember Member)>();
            bool anyKnownStatic = false;
            foreach (var ifaceTy in ifaces)
            {
                if (!_interfaces.TryGetValue(ifaceTy.Name, out var iface)) continue;
                if (iface.StaticMembers is null) continue;
                anyKnownStatic = true;
                foreach (var (name, member) in iface.StaticMembers)
                    ifaceStatic[name] = (iface.Name, member);
            }

            // 1) For each class static method: classify by (IsOverride, target exists).
            var classOverrides = new HashSet<string>();
            foreach (var m in cls.Methods.Where(mm => mm.IsStatic))
            {
                if (m.IsOverride)
                {
                    if (!ifaceStatic.TryGetValue(m.Name, out var target))
                    {
                        if (anyKnownStatic)
                            _diags.Error(DiagnosticCodes.InterfaceMismatch,
                                $"`{cls.Name}.{m.Name}`: no matching `static abstract` / `static virtual` member in any implemented interface",
                                m.Span);
                        // else: no known interface has static info; trust the `override`.
                        continue;
                    }
                    if (target.Member.Kind == StaticMemberKind.Concrete)
                    {
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"`{cls.Name}.{m.Name}`: interface `{target.Iface}.{m.Name}` is sealed (no `virtual`/`abstract`), cannot be overridden",
                            m.Span);
                        continue;
                    }
                    // Signature check: param count + return assignability with T-tolerance
                    var ifaceSig = target.Member.Signature;
                    if (m.Params.Count != ifaceSig.Params.Count)
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"`{cls.Name}.{m.Name}` has wrong parameter count overriding `{target.Iface}.{m.Name}` " +
                            $"(expected {ifaceSig.Params.Count}, got {m.Params.Count})", m.Span);
                    classOverrides.Add(m.Name);
                }
                else if (ifaceStatic.TryGetValue(m.Name, out var targetNoOv)
                      && targetNoOv.Member.Kind != StaticMemberKind.Concrete)
                {
                    // User wrote `static T op_X(...)` without `override` but an interface
                    // member with the same name exists → missing override keyword.
                    _diags.Error(DiagnosticCodes.InterfaceMismatch,
                        $"`{cls.Name}.{m.Name}` hides interface member `{targetNoOv.Iface}.{m.Name}` — add `override` or rename",
                        m.Span);
                }
            }

            // 2) Check abstract interface statics are all overridden.
            if (cls.IsAbstract) continue;
            foreach (var (name, (ifaceName, member)) in ifaceStatic)
            {
                if (member.Kind == StaticMemberKind.Abstract && !classOverrides.Contains(name))
                    _diags.Error(DiagnosticCodes.InterfaceMismatch,
                        $"class `{cls.Name}` must `static override` abstract member `{ifaceName}.{name}`",
                        cls.Span);
            }
        }
    }

    /// Globally re-run inheritance field/method merge for every class in `_classes`,
    /// in topological order (base → derived). Idempotent.
    ///
    /// Per-CU `CollectClasses` second pass only sees classes registered up to that
    /// point; when CUs are processed out of inheritance order (e.g. derived
    /// `ArgumentNullException.z42` before its base `ArgumentException.z42`), the
    /// per-CU merge silently skips because the base isn't in `_classes` yet.
    /// This pass walks every class's base chain and merges, ensuring all derived
    /// classes carry their full inherited Fields/Methods regardless of CU order.
    /// (fix-package-compiler-cross-file 引入 — 修复多 CU 包内多级继承。)
    public void FinalizeInheritance()
    {
        var done = new HashSet<string>();
        foreach (var name in _classes.Keys.ToList())
            FinalizeInheritanceOne(name, done);
        // All CUs collected → upgrade any forward/cross-file class references that
        // degraded to Z42PrimType during per-CU resolution (SymbolCollector.TypeFixup).
        // Runs after inheritance so inherited members are covered too.
        FinalizeTypeReferences();
    }

    /// fix-memorystream-override-visibility (2026-05-24): run deferred `override`-method
    /// validation now that all CUs are collected and `_virtualMethods` / `_classInterfaces`
    /// for every class in the package is populated. Must run AFTER `FinalizeInheritance()`
    /// so the base chain walk via `_classes[cur].BaseClassName` is consistent.
    ///
    /// Same diagnostic logic as the original per-CU block (E0411 InvalidInheritance):
    ///   1. Walk base chain; if any ancestor's `_virtualMethods` contains the method → ok
    ///   2. Otherwise check declared interface methods → ok
    ///   3. Otherwise emit "no matching virtual or abstract method in base class"
    public void FinalizeOverrideChecks()
    {
        foreach (var (clsKey, cls) in _pendingOverrideChecks)
        {
            var effectiveBase = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            if (effectiveBase == null) continue;

            foreach (var m in cls.Methods.Where(m => m.IsOverride && !m.IsStatic))
            {
                bool found = false;
                var  cur   = effectiveBase;
                while (cur != null)
                {
                    if (_virtualMethods.TryGetValue(cur, out var vset) && vset.Contains(m.Name))
                    { found = true; break; }
                    cur = _classes.TryGetValue(cur, out var ct) ? ct.BaseClassName : null;
                }
                if (!found && _classInterfaces.TryGetValue(clsKey, out var ifaces))
                {
                    foreach (var iface in ifaces)
                    {
                        if (_interfaces.TryGetValue(iface.Name, out var it) && it.Methods.ContainsKey(m.Name))
                        { found = true; break; }
                    }
                }
                if (!found)
                    _diags.Error(DiagnosticCodes.InvalidInheritance,
                        $"`{cls.Name}.{m.Name}`: no matching virtual or abstract method in base class", m.Span);
            }
        }
        _pendingOverrideChecks.Clear();
    }

    private void FinalizeInheritanceOne(string name, HashSet<string> done)
    {
        if (!done.Add(name)) return;
        if (!_classes.TryGetValue(name, out var ct)) return;
        if (ct.BaseClassName is not { } baseName) return;
        FinalizeInheritanceOne(baseName, done);
        if (!_classes.TryGetValue(baseName, out var baseType)) return;
        var mergedFields  = new Dictionary<string, IFieldSymbol>(baseType.Fields);
        var mergedMethods = new Dictionary<string, IMethodSymbol>(baseType.Methods);
        foreach (var kv in ct.Fields)  mergedFields[kv.Key]  = kv.Value;
        foreach (var kv in ct.Methods) mergedMethods[kv.Key] = kv.Value;
        _classes[name] = ct with { Fields = mergedFields, Methods = mergedMethods };
    }

    /// Two-phase construction fix-up: after Z42ClassType is built with Symbol-typed
    /// dicts, assign ContainingType on each Symbol so consumers (Phase 4 BoundCall.Symbol
    /// readers, Phase 5 TestAttributeValidator etc.) can navigate symbol → containing type.
    private static void FixupContainingType(IReadOnlyList<IMemberSymbol> symbols, Z42ClassType containingClass)
    {
        foreach (var sym in symbols)
        {
            switch (sym)
            {
                case MethodSymbol ms: ms.ContainingType = containingClass; break;
                case FieldSymbol  fs: fs.ContainingType = containingClass; break;
            }
        }
    }
}
