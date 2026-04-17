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
        Dictionary<string, HashSet<string>> virtualMethods)
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
    }

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
