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
/// L3-G4d: `TypeParams` carries generic parameter names for `class Foo<T, U>` so
/// imported classes can be instantiated with type arguments from consumer code.
/// L3-G3d: `TypeParamConstraints` carries `where` clauses so the consumer
/// TypeChecker can validate `new ImportedGeneric<T>()` at compile time.
public sealed record ExportedClassDef(
    string Name,
    string? BaseClass,
    bool IsAbstract,
    bool IsSealed,
    bool IsStatic,
    List<ExportedFieldDef>  Fields,
    List<ExportedMethodDef> Methods,
    List<string> Interfaces,
    List<string>? TypeParams = null,
    List<ExportedTypeParamConstraint>? TypeParamConstraints = null);

/// Serialized `where` bundle for a single type parameter. (L3-G3d)
/// Interface / base-class references stored by name only (no type args); matches
/// the TypeChecker's current validation granularity for cross-zpkg usage.
/// L3-G2.5 ctor: `RequiresConstructor` carries `where T: new()`.
/// L3-G2.5 enum: `RequiresEnum` carries `where T: enum`.
public sealed record ExportedTypeParamConstraint(
    string TypeParam,
    List<string> Interfaces,
    string? BaseClass,
    string? TypeParamRef,
    bool RequiresClass,
    bool RequiresStruct,
    bool RequiresConstructor = false,
    bool RequiresEnum = false);

/// Exported interface definition.
public sealed record ExportedInterfaceDef(
    string Name,
    List<ExportedMethodDef> Methods,
    /// Generic type parameter names (e.g. `["T"]` for `INumber<T>`). Used on the
    /// consumer side to restore method-signature occurrences of `T` as
    /// `Z42GenericParamType` rather than falling back to `Z42PrimType("T")`.
    List<string>? TypeParams = null);

/// Exported enum definition.
public sealed record ExportedEnumDef(
    string Name,
    List<ExportedEnumMember> Members);

public sealed record ExportedEnumMember(string Name, long Value);

/// Exported top-level function definition.
/// L3-G3d: `TypeParamConstraints` mirrors the class variant for generic functions.
public sealed record ExportedFuncDef(
    string Name,
    List<ExportedParamDef> Params,
    string ReturnType,
    int MinArgCount,
    List<string>? TypeParams = null,
    List<ExportedTypeParamConstraint>? TypeParamConstraints = null);

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
