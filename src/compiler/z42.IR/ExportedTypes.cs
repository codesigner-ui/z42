namespace Z42.IR;

/// <summary>
/// Type signature metadata for cross-package reference compilation.
/// Serialized into the zpkg TSIG section by ZpkgWriter; deserialized by ZpkgReader.
/// Used to reconstruct Z42Type objects in the TypeChecker when compiling against dependencies.
/// </summary>

/// A collection of exported type signatures from a single namespace within a zpkg.
/// L3-Impl2: `Impls` carries cross-zpkg `impl Trait for Type { ... }` declarations so
/// downstream TypeCheckers see methods/traits added to imported types.
public sealed record ExportedModule(
    string Namespace,
    List<ExportedClassDef>     Classes,
    List<ExportedInterfaceDef> Interfaces,
    List<ExportedEnumDef>      Enums,
    List<ExportedFuncDef>      Functions,
    List<ExportedImplDef>?     Impls     = null,
    /// 2026-05-02 add-generic-delegates (D1c): exported delegate types so
    /// downstream CUs can resolve `Func<int,int>` / `Predicate<T>` etc.
    /// Replaces SymbolCollector hardcoded `Action`/`Func` desugar.
    List<ExportedDelegateDef>? Delegates = null);

/// 2026-05-02 add-generic-delegates: serialized `delegate R Foo<T,R>(T arg)`.
/// Param + return types stored as IR-level type-name strings (same as
/// ExportedFuncDef); generic type-params restored on the consumer side as
/// `Z42GenericParamType` placeholders.
public sealed record ExportedDelegateDef(
    string Name,
    List<ExportedParamDef> Params,
    string ReturnType,
    List<string>? TypeParams = null,
    /// Simple class name when this delegate is nested inside a class
    /// (`class Btn { delegate void OnClick(...) }` → ContainerClass="Btn").
    /// Null for top-level delegates.
    string? ContainerClass = null);

/// L3-Impl2: serialized `impl Trait for Target { ... }` block.
/// `TargetFqName` and `TraitFqName` are fully-qualified (e.g. `Std.int`,
/// `Std.INumber`) so consumers across zpkg boundaries can find target/trait
/// in the merged imported symbol table. Method bodies are NOT in this record —
/// they live in the regular MODS section under `{TargetFqName}.{Method}`.
public sealed record ExportedImplDef(
    string TargetFqName,
    string TraitFqName,
    List<string> TraitTypeArgs,
    List<ExportedMethodDef> Methods);

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
