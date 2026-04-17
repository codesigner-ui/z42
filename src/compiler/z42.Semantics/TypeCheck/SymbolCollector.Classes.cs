using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

internal sealed partial class SymbolCollector
{
    /// True if the class should NOT implicitly inherit from Object.
    private static bool ExcludeFromImplicitObject(ClassDecl cls) =>
        cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name);

    private void CollectClasses(CompilationUnit cu)
    {
        // Pre-register Object's virtual methods and class shape
        bool cuDefinesObject = cu.Classes.Any(c => WellKnownNames.IsObjectClass(c.Name));
        _virtualMethods["Object"] = ["ToString", "Equals", "GetHashCode"];
        if (!cuDefinesObject)
        {
            var objectMethods = new Dictionary<string, Z42FuncType>
            {
                ["ToString"]    = new([], Z42Type.String),
                ["Equals"]      = new([Z42Type.Object], Z42Type.Bool),
                ["GetHashCode"] = new([], Z42Type.Int),
            };
            _classes["Object"] = new Z42ClassType(
                "Object",
                new Dictionary<string, Z42Type>(),
                objectMethods,
                new Dictionary<string, Z42Type>(),
                new Dictionary<string, Z42FuncType>(),
                new Dictionary<string, Visibility>(),
                null);
        }

        // Pre-pass: register every class name as an empty stub
        foreach (var cls in cu.Classes)
        {
            var effectiveBase = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");

            if (_classes.ContainsKey(cls.Name))
                _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                    $"duplicate class declaration `{cls.Name}`", cls.Span);
            else
                _classes[cls.Name] = new Z42ClassType(
                    cls.Name, new Dictionary<string, Z42Type>(),
                    new Dictionary<string, Z42FuncType>(),
                    new Dictionary<string, Z42Type>(),
                    new Dictionary<string, Z42FuncType>(),
                    new Dictionary<string, Visibility>(),
                    effectiveBase);
        }

        // First pass: collect own fields and methods
        foreach (var cls in cu.Classes)
        {
            if (cls.IsStruct && cls.BaseClass != null)
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"struct `{cls.Name}` cannot inherit from a base class", cls.Span);
            if (cls.IsStruct && cls.Interfaces.Count > 0)
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"struct `{cls.Name}` cannot implement interfaces", cls.Span);

            var fields        = new Dictionary<string, Z42Type>();
            var staticFields  = new Dictionary<string, Z42Type>();
            var methods       = new Dictionary<string, Z42FuncType>();
            var staticMethods = new Dictionary<string, Z42FuncType>();

            foreach (var f in cls.Fields)
            {
                var ft = ResolveType(f.Type);
                if (f.IsStatic) staticFields[f.Name] = ft;
                else            fields[f.Name]        = ft;
            }
            var methodNameCount = cls.Methods
                .GroupBy(m => (m.Name, m.IsStatic))
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var m in cls.Methods)
            {
                var retType = m.Name == cls.Name ? (Z42Type)Z42Type.Void : ResolveType(m.ReturnType);
                var sig     = BuildFuncSignature(m.Params, retType);
                bool isOverloaded = methodNameCount[(m.Name, m.IsStatic)] > 1;
                string regName = isOverloaded ? $"{m.Name}${m.Params.Count}" : m.Name;
                if (m.IsStatic) staticMethods[regName] = sig;
                else            methods[regName]        = sig;
            }
            var memberVis = new Dictionary<string, Visibility>();
            foreach (var f in cls.Fields)  memberVis[f.Name] = f.Visibility;
            foreach (var m in cls.Methods) memberVis[m.Name] = m.Visibility;

            var effectiveBase2 = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            _classes[cls.Name] = new Z42ClassType(
                cls.Name, fields, methods, staticFields, staticMethods,
                memberVis, effectiveBase2);
            _classInterfaces[cls.Name] = cls.Interfaces.ToHashSet();

            if (cls.IsAbstract) _abstractClasses.Add(cls.Name);
            if (cls.IsSealed)   _sealedClasses.Add(cls.Name);
            _abstractMethods[cls.Name] = cls.Methods
                .Where(m => m.IsAbstract).Select(m => m.Name).ToHashSet();
            _virtualMethods[cls.Name] = cls.Methods
                .Where(m => m.IsVirtual || m.IsAbstract).Select(m => m.Name).ToHashSet();
        }

        // Second pass: merge inherited fields/methods
        foreach (var cls in cu.Classes)
        {
            var effectiveBase3 = cls.BaseClass
                ?? (ExcludeFromImplicitObject(cls) ? null : "Object");
            if (effectiveBase3 == null) continue;

            if (_sealedClasses.Contains(effectiveBase3))
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"cannot inherit from sealed class `{effectiveBase3}`", cls.Span);

            foreach (var m in cls.Methods.Where(m => m.IsOverride))
            {
                bool found = false;
                var  cur   = effectiveBase3;
                while (cur != null)
                {
                    if (_virtualMethods.TryGetValue(cur, out var vset) && vset.Contains(m.Name))
                    { found = true; break; }
                    cur = _classes.TryGetValue(cur, out var ct) ? ct.BaseClassName : null;
                }
                if (!found && _classInterfaces.TryGetValue(cls.Name, out var ifaces))
                {
                    foreach (var iface in ifaces)
                    {
                        if (_interfaces.TryGetValue(iface, out var it) && it.Methods.ContainsKey(m.Name))
                        { found = true; break; }
                    }
                }
                if (!found)
                    _diags.Error(DiagnosticCodes.InvalidInheritance,
                        $"`{cls.Name}.{m.Name}`: no matching virtual or abstract method in base class", m.Span);
            }

            if (!_classes.TryGetValue(effectiveBase3, out var baseType)) continue;
            var derived = _classes[cls.Name];
            var mergedFields  = new Dictionary<string, Z42Type>(baseType.Fields);
            var mergedMethods = new Dictionary<string, Z42FuncType>(baseType.Methods);
            foreach (var kv in derived.Fields)  mergedFields[kv.Key]  = kv.Value;
            foreach (var kv in derived.Methods) mergedMethods[kv.Key] = kv.Value;
            _classes[cls.Name] = derived with { Fields = mergedFields, Methods = mergedMethods };

            var baseAbstract = _abstractMethods.GetValueOrDefault(effectiveBase3, []);
            var ownMethods   = cls.Methods.Select(m => m.Name).ToHashSet();
            var remaining    = baseAbstract.Except(ownMethods).ToHashSet();
            _abstractMethods[cls.Name] = [.._abstractMethods.GetValueOrDefault(cls.Name, []), ..remaining];
        }

        // Third pass: concrete classes must implement all abstract methods
        foreach (var cls in cu.Classes)
        {
            if (cls.IsAbstract) continue;
            if (_abstractMethods.TryGetValue(cls.Name, out var unimpl) && unimpl.Count > 0)
                _diags.Error(DiagnosticCodes.InvalidInheritance,
                    $"class `{cls.Name}` must implement abstract method(s): {string.Join(", ", unimpl.Select(m => $"`{m}`"))}",
                    cls.Span);
        }

        // Fourth pass: verify interface implementation completeness
        foreach (var cls in cu.Classes)
        {
            if (cls.IsAbstract) continue;
            if (!_classInterfaces.TryGetValue(cls.Name, out var ifaces) || ifaces.Count == 0) continue;
            if (!_classes.TryGetValue(cls.Name, out var classType)) continue;

            foreach (var ifaceName in ifaces)
            {
                if (!_interfaces.TryGetValue(ifaceName, out var iface)) continue;
                foreach (var (methodName, ifaceSig) in iface.Methods)
                {
                    if (!classType.Methods.TryGetValue(methodName, out var implSig))
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` does not implement interface method `{ifaceName}.{methodName}`",
                            cls.Span);
                    else if (implSig.Params.Count != ifaceSig.Params.Count)
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` method `{methodName}` has wrong parameter count for interface `{ifaceName}` " +
                            $"(expected {ifaceSig.Params.Count}, got {implSig.Params.Count})",
                            cls.Span);
                    else if (!Z42Type.IsAssignableTo(ifaceSig.Ret, implSig.Ret))
                        _diags.Error(DiagnosticCodes.InterfaceMismatch,
                            $"class `{cls.Name}` method `{methodName}` return type `{implSig.Ret}` does not match interface `{ifaceName}` return type `{ifaceSig.Ret}`",
                            cls.Span);
                }
            }
        }
    }
}
