namespace Z42.IR;

/// <summary>Shared well-known name checks used by both TypeChecker and IrGen.</summary>
public static class WellKnownNames
{
    /// <summary>Returns <c>true</c> if <paramref name="name"/> refers to the root Object class.</summary>
    public static bool IsObjectClass(string name) => name is "Object" or "Std.Object";
}
