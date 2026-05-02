namespace Z42.Semantics.TypeCheck;

/// <summary>
/// 2026-05-02 add-delegate-type. Per-delegate type metadata held by
/// <see cref="SymbolTable.Delegates"/>. Single source of truth for resolving
/// `delegate R Foo<T>(T arg);` declarations into runtime <see cref="Z42FuncType"/>
/// instances when used as type expressions.
/// </summary>
/// <param name="Signature">Resolved <see cref="Z42FuncType"/>; for generic delegates
/// the params / return contain <see cref="Z42GenericParamType"/> placeholders that
/// <c>SubstituteTypeParams</c> rewrites at instantiation time.</param>
/// <param name="TypeParams">Type parameter names in declaration order (empty for non-generic).</param>
/// <param name="ContainerClass">Simple class name that contains this delegate when nested,
/// otherwise <see langword="null"/> for top-level delegates.</param>
public sealed record DelegateInfo(
    Z42FuncType Signature,
    IReadOnlyList<string> TypeParams,
    string? ContainerClass);
