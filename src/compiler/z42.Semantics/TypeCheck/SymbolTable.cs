using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Readonly snapshot of all type shapes collected during Pass 0.
/// Serves as the explicit data boundary between symbol collection and body binding.
///
/// Future: when TypeChecker is fully split, SymbolCollector produces this,
/// and BodyBinder consumes it without access to mutable collection state.
/// </summary>
public sealed class SymbolTable
{
    public IReadOnlyDictionary<string, Z42ClassType> Classes { get; }
    public IReadOnlyDictionary<string, Z42FuncType> Functions { get; }
    public IReadOnlyDictionary<string, Z42InterfaceType> Interfaces { get; }
    public IReadOnlyDictionary<string, long> EnumConstants { get; }
    public IReadOnlySet<string> EnumTypes { get; }
    public IReadOnlyDictionary<string, HashSet<string>> ClassInterfaces { get; }
    public IReadOnlyDictionary<string, HashSet<string>> AbstractMethods { get; }
    public IReadOnlySet<string> AbstractClasses { get; }
    public IReadOnlySet<string> SealedClasses { get; }
    public IReadOnlyDictionary<string, HashSet<string>> VirtualMethods { get; }

    /// Names of classes that were imported from dependency zpkgs (not locally defined).
    public IReadOnlySet<string> ImportedClassNames { get; }

    /// Maps imported class short name → its original namespace (e.g. "Console" → "Std.IO").
    /// Used by IrGen to qualify imported class calls with the correct dependency namespace.
    public IReadOnlyDictionary<string, string> ImportedClassNamespaces { get; }

    internal SymbolTable(
        Dictionary<string, Z42ClassType> classes,
        Dictionary<string, Z42FuncType> functions,
        Dictionary<string, Z42InterfaceType> interfaces,
        Dictionary<string, long> enumConstants,
        HashSet<string> enumTypes,
        Dictionary<string, HashSet<string>> classInterfaces,
        Dictionary<string, HashSet<string>> abstractMethods,
        HashSet<string> abstractClasses,
        HashSet<string> sealedClasses,
        Dictionary<string, HashSet<string>> virtualMethods,
        HashSet<string>? importedClassNames = null,
        Dictionary<string, string>? importedClassNamespaces = null)
    {
        Classes = classes;
        Functions = functions;
        Interfaces = interfaces;
        EnumConstants = enumConstants;
        EnumTypes = enumTypes;
        ClassInterfaces = classInterfaces;
        AbstractMethods = abstractMethods;
        AbstractClasses = abstractClasses;
        SealedClasses = sealedClasses;
        VirtualMethods = virtualMethods;
        ImportedClassNames = importedClassNames ?? new HashSet<string>();
        ImportedClassNamespaces = importedClassNamespaces ?? new Dictionary<string, string>();
    }

    /// Resolve a TypeExpr to a Z42Type using the frozen symbol table.
    public Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType      => Z42Type.Void,
        OptionType ot => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at => new Z42ArrayType(ResolveType(at.Element)),
        NamedType  nt => nt.Name switch
        {
            "int"    or "i32" => Z42Type.Int,
            "long"   or "i64" => Z42Type.Long,
            "float"  or "f32" => Z42Type.Float,
            "double" or "f64" => Z42Type.Double,
            "bool"            => Z42Type.Bool,
            "string"          => Z42Type.String,
            "char"            => Z42Type.Char,
            "object"          => Z42Type.Object,
            "void"            => Z42Type.Void,
            "var"             => Z42Type.Unknown,
            "i8"              => Z42Type.I8,
            "i16"             => Z42Type.I16,
            "u8"              => Z42Type.U8,
            "u16"             => Z42Type.U16,
            "u32"             => Z42Type.U32,
            "u64"             => Z42Type.U64,
            "sbyte"           => Z42Type.I8,
            "short"           => Z42Type.I16,
            "byte"            => Z42Type.U8,
            "ushort"          => Z42Type.U16,
            "uint"            => Z42Type.U32,
            "ulong"           => Z42Type.U64,
            _                 => Classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : Interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name),
        },
        _ => Z42Type.Unknown
    };

    /// Query: is <paramref name="derived"/> a subclass of <paramref name="baseClass"/>?
    public bool IsSubclassOf(string derived, string baseClass)
    {
        var cur = derived;
        while (cur != null)
        {
            if (Classes.TryGetValue(cur, out var ct))
            {
                cur = ct.BaseClassName;
                if (cur == baseClass) return true;
            }
            else break;
        }
        return false;
    }

    /// Query: does <paramref name="className"/> implement <paramref name="ifaceName"/>?
    public bool ImplementsInterface(string className, string ifaceName)
    {
        var cur = className;
        while (cur != null)
        {
            if (ClassInterfaces.TryGetValue(cur, out var ifaces) && ifaces.Contains(ifaceName))
                return true;
            cur = Classes.TryGetValue(cur, out var ct) ? ct.BaseClassName : null;
        }
        return false;
    }
}
