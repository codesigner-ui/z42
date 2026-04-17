namespace Z42.IR;

/// <summary>
/// Type signature metadata for cross-package reference compilation.
/// Serialized into the zpkg TSIG section by ZpkgWriter; deserialized by ZpkgReader.
/// Used to reconstruct Z42Type objects in the TypeChecker when compiling against dependencies.
/// </summary>

/// A collection of exported type signatures from a single namespace within a zpkg.
public sealed record ExportedModule(
    string Namespace,
    List<ExportedClassDef>     Classes,
    List<ExportedInterfaceDef> Interfaces,
    List<ExportedEnumDef>      Enums,
    List<ExportedFuncDef>      Functions);

/// Exported class/struct/record definition with full member signatures.
public sealed record ExportedClassDef(
    string Name,
    string? BaseClass,
    bool IsAbstract,
    bool IsSealed,
    bool IsStatic,
    List<ExportedFieldDef>  Fields,
    List<ExportedMethodDef> Methods,
    List<string> Interfaces);

/// Exported interface definition.
public sealed record ExportedInterfaceDef(
    string Name,
    List<ExportedMethodDef> Methods);

/// Exported enum definition.
public sealed record ExportedEnumDef(
    string Name,
    List<ExportedEnumMember> Members);

public sealed record ExportedEnumMember(string Name, long Value);

/// Exported top-level function definition.
public sealed record ExportedFuncDef(
    string Name,
    List<ExportedParamDef> Params,
    string ReturnType,
    int MinArgCount);

/// Exported method (class instance/static method, interface method).
public sealed record ExportedMethodDef(
    string Name,
    List<ExportedParamDef> Params,
    string ReturnType,
    string Visibility,
    bool IsStatic,
    bool IsVirtual,
    bool IsAbstract,
    int MinArgCount);

/// Exported field (class instance/static field).
public sealed record ExportedFieldDef(
    string Name,
    string TypeName,
    string Visibility,
    bool IsStatic);

/// Exported parameter definition.
public sealed record ExportedParamDef(string Name, string TypeName);
