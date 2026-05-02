namespace Z42.IR;

// ── IR type system ───────────────────────────────────────────────────────────

/// Runtime type tag for each register value. Mapped from Z42Type during codegen.
public enum IrType : byte
{
    Unknown = 0,
    I8, I16, I32, I64,
    U8, U16, U32, U64,
    F32, F64,
    Bool, Char, Str,
    Ref,      // any heap object (class instance, array, list, dict, null)
    Void,
}

/// A typed register reference: register ID + its static type.
public readonly record struct TypedReg(int Id, IrType Type = IrType.Unknown);

// ── Module ────────────────────────────────────────────────────────────────────

/// Root bytecode module — serialized to zbc binary format.
public sealed record IrModule(
    string Name,
    IReadOnlyList<string> StringPool,
    List<IrClassDesc> Classes,
    List<IrFunction> Functions,
    /// <summary>R1 (add-test-metadata-section) — entries collected from
    /// <c>z42.test.*</c> attributes. Persisted to the zbc <c>TIDX</c> section
    /// only when non-empty (writer skips section when null/empty; reader returns
    /// empty list when section absent). Default null preserves backward
    /// compatibility with all existing callers.</summary>
    IReadOnlyList<TestEntry>? TestIndex = null);

// ── Class descriptor ──────────────────────────────────────────────────────────

public sealed record IrClassDesc(
    string Name,
    string? BaseClass,
    List<IrFieldDesc> Fields,
    List<string>? TypeParams = null,
    List<IrConstraintBundle>? TypeParamConstraints = null);

public sealed record IrFieldDesc(string Name, string Type);

/// Resolved constraint bundle for one generic type parameter.
/// Mirrors `GenericConstraintBundle` from the semantic layer but uses plain strings
/// for serialization. (L3-G3a, L3-G2.5)
public sealed record IrConstraintBundle(
    bool RequiresClass,
    bool RequiresStruct,
    string? BaseClass,
    List<string> Interfaces,
    /// L3-G2.5 bare-typeparam: name of another type parameter in the same decl
    /// that this parameter must be a subtype of. Null when no such constraint.
    string? TypeParamConstraint = null,
    /// L3-G2.5 ctor: `where T: new()` — T must have a no-arg constructor.
    bool RequiresConstructor = false,
    /// L3-G2.5 enum: `where T: enum` — T must be an enum type.
    bool RequiresEnum = false)
{
    public bool IsEmpty => !RequiresClass && !RequiresStruct
                           && BaseClass is null && Interfaces.Count == 0
                           && TypeParamConstraint is null
                           && !RequiresConstructor
                           && !RequiresEnum;
}

// ── Function ──────────────────────────────────────────────────────────────────

public sealed record IrFunction(
    string Name,
    int ParamCount,
    string RetType,
    string ExecMode,
    List<IrBlock> Blocks,
    List<IrExceptionEntry>? ExceptionTable = null,
    bool IsStatic = false,
    /// Total number of registers used by this function (size for Vec pre-allocation in the VM).
    /// 0 means unknown (VM falls back to dynamic resizing).
    int MaxReg = 0,
    /// Source-line mapping table. Each entry records the source line number
    /// at a given (block, instruction) position. Run-length encoded (entry only
    /// when the line changes). Used by the VM to show source locations in errors.
    List<IrLineEntry>? LineTable = null,
    /// Debug info: maps register IDs to source-level variable names.
    List<IrLocalVarEntry>? LocalVarTable = null,
    /// Generic type parameter names: ["T"], ["K", "V"], etc. Null for non-generic functions.
    List<string>? TypeParams = null,
    /// Resolved constraints per type parameter (L3-G3a). Aligned by index with
    /// `TypeParams` when both are non-null. Each entry may be "empty" (no flags/base/interfaces)
    /// to signal that the parameter is unconstrained.
    List<IrConstraintBundle>? TypeParamConstraints = null);

/// An entry in a function's local variable table: register RegId holds variable Name.
public sealed record IrLocalVarEntry(string Name, int RegId);

/// An entry in a function's line number table.
/// "From (BlockIdx, InstrIdx) onward, the source line is Line in File."
public sealed record IrLineEntry(int BlockIdx, int InstrIdx, int Line, string? File = null);

/// One entry in a function's exception table: covers blocks [TryStart, TryEnd)
/// and redirects unhandled throws to CatchLabel, storing the exception in CatchReg.
public sealed record IrExceptionEntry(
    string TryStart,
    string TryEnd,
    string CatchLabel,
    string? CatchType,
    TypedReg CatchReg);

// ── Basic block ───────────────────────────────────────────────────────────────

public sealed record IrBlock(
    string Label,
    List<IrInstr> Instructions,
    IrTerminator Terminator);

// ── Instructions ──────────────────────────────────────────────────────────

public abstract record IrInstr;

// ── Constants ─────────────────────────────────────────────────────────────────

public sealed record ConstStrInstr(TypedReg Dst, int Idx)         : IrInstr;
public sealed record ConstI32Instr(TypedReg Dst, int Val)         : IrInstr;
public sealed record ConstI64Instr(TypedReg Dst, long Val)        : IrInstr;
public sealed record ConstF64Instr(TypedReg Dst, double Val)      : IrInstr;
public sealed record ConstBoolInstr(TypedReg Dst, bool Val)       : IrInstr;
public sealed record ConstCharInstr(TypedReg Dst, char Val)       : IrInstr;
/// Loads a null value into Dst.
public sealed record ConstNullInstr(TypedReg Dst)                 : IrInstr;

// ── Data movement ─────────────────────────────────────────────────────────────

/// Copies the value of register Src into Dst.
public sealed record CopyInstr(TypedReg Dst, TypedReg Src)             : IrInstr;
public sealed record StrConcatInstr(TypedReg Dst, TypedReg A, TypedReg B) : IrInstr;
public sealed record ToStrInstr(TypedReg Dst, TypedReg Src)            : IrInstr;

// ── Calls ─────────────────────────────────────────────────────────────────────

public sealed record CallInstr(TypedReg Dst, string Func, List<TypedReg> Args) : IrInstr;
public sealed record BuiltinInstr(TypedReg Dst, string Name, List<TypedReg> Args) : IrInstr;
/// Push a function reference value onto the operand stack. L2 no-capture lambda
/// literal lowers to this instruction with `Func` pointing at a lifted function.
/// See docs/design/closure.md §6 + ir.md.
public sealed record LoadFnInstr(TypedReg Dst, string Func) : IrInstr;
/// Indirect call: invoke a function via a `FuncRef`-typed register. Used when
/// a local variable holds a lambda or function reference. Args follow the
/// same convention as `CallInstr`. See docs/design/closure.md §6.
public sealed record CallIndirectInstr(TypedReg Dst, TypedReg Callee, List<TypedReg> Args) : IrInstr;
/// L3 closure tier-C: allocate an env with the captured values and construct
/// a closure value `(env, fn_name)` in `Dst`. Each `Captures` reg supplies
/// one captured value (in capture order, matching the lifted function's env
/// layout). See docs/design/closure.md §6 + impl-closure-l3-core design Decision 7.
///
/// `StackAlloc`（2026-05-02 impl-closure-l3-escape-stack）：编译器 escape 分析
/// 证明 env 不离开当前 frame 时设为 true → VM 走 frame-local arena
/// （`Value::StackClosure { env_idx, fn_name }`，零堆分配）；否则走堆
/// （`Value::Closure { env: GcRef, fn_name }`，原 Tier C 路径）。
public sealed record MkClosInstr(
    TypedReg Dst, string FuncName, List<TypedReg> Captures,
    bool StackAlloc = false) : IrInstr;

// ── Arithmetic ────────────────────────────────────────────────────────────────

public sealed record AddInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record SubInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record MulInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record DivInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record RemInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;

// ── Comparison ────────────────────────────────────────────────────────────────

public sealed record EqInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record NeInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record LtInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record LeInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record GtInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record GeInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;

// ── Logical ───────────────────────────────────────────────────────────────────

public sealed record AndInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record OrInstr(TypedReg Dst, TypedReg A, TypedReg B)     : IrInstr;
public sealed record NotInstr(TypedReg Dst, TypedReg Src)              : IrInstr;
public sealed record NegInstr(TypedReg Dst, TypedReg Src)              : IrInstr;

// ── Bitwise ───────────────────────────────────────────────────────────────────

public sealed record BitAndInstr(TypedReg Dst, TypedReg A, TypedReg B) : IrInstr;
public sealed record BitOrInstr(TypedReg Dst, TypedReg A, TypedReg B)  : IrInstr;
public sealed record BitXorInstr(TypedReg Dst, TypedReg A, TypedReg B) : IrInstr;
public sealed record BitNotInstr(TypedReg Dst, TypedReg Src)           : IrInstr;
public sealed record ShlInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;
public sealed record ShrInstr(TypedReg Dst, TypedReg A, TypedReg B)    : IrInstr;

// ── Array instructions ───────────────────────────────────────────────────────

/// Allocate a zero-initialized array of Size elements.
public sealed record ArrayNewInstr(TypedReg Dst, TypedReg Size)              : IrInstr;
/// Allocate an array from a list of element registers.
public sealed record ArrayNewLitInstr(TypedReg Dst, List<TypedReg> Elems)    : IrInstr;
/// Load element at index Idx from array Arr into Dst.
public sealed record ArrayGetInstr(TypedReg Dst, TypedReg Arr, TypedReg Idx) : IrInstr;
/// Store Val into array Arr at index Idx (no result register).
public sealed record ArraySetInstr(TypedReg Arr, TypedReg Idx, TypedReg Val) : IrInstr;
/// Get the length of array Arr as i32 into Dst.
public sealed record ArrayLenInstr(TypedReg Dst, TypedReg Arr)               : IrInstr;

// ── Object instructions ──────────────────────────────────────────────────────

/// Allocate a new object of ClassName, calling specific overload-resolved
/// constructor `CtorName` (含 `$N` arity suffix 如有，对齐 Call 指令的
/// FQ 函数名约定）with Args. VM 不再做名字推断，直查 ctor_name。
public sealed record ObjNewInstr(TypedReg Dst, string ClassName, string CtorName, List<TypedReg> Args) : IrInstr;
/// Load field FieldName from object in register Obj into Dst.
public sealed record FieldGetInstr(TypedReg Dst, TypedReg Obj, string FieldName)      : IrInstr;
/// Store Val into field FieldName of object in register Obj.
public sealed record FieldSetInstr(TypedReg Obj, string FieldName, TypedReg Val)      : IrInstr;
/// Virtual dispatch: call Method on the runtime class of Obj, walking base classes.
public sealed record VCallInstr(TypedReg Dst, TypedReg Obj, string Method, List<TypedReg> Args) : IrInstr;
/// `expr is ClassName` — returns bool (true if Obj's runtime type is ClassName or a subclass).
public sealed record IsInstanceInstr(TypedReg Dst, TypedReg Obj, string ClassName) : IrInstr;
/// `expr as ClassName` — returns the object if it is an instance of ClassName (or subclass), else null.
public sealed record AsCastInstr(TypedReg Dst, TypedReg Obj, string ClassName) : IrInstr;
/// Load the module-level static field named Field into Dst.
public sealed record StaticGetInstr(TypedReg Dst, string Field) : IrInstr;
/// Store Val into the module-level static field named Field.
public sealed record StaticSetInstr(string Field, TypedReg Val) : IrInstr;

// ── Native Interop (C1 scaffold; semantics by C2/C4/C5) ───────────────────

/// Direct native symbol call. `Module`/`TypeName`/`Symbol` identify a
/// function registered through the Tier 1 ABI; resolved at load time.
/// `Args` are blittable z42 values per spec C2.
public sealed record CallNativeInstr(
    TypedReg Dst, string Module, string TypeName, string Symbol, List<TypedReg> Args
) : IrInstr;

/// Indirect call through a native type's vtable. `Recv` is a native-type
/// instance; `VtableSlot` is the method index (filled by C5 source generator
/// at compile time, so no runtime name lookup is needed).
public sealed record CallNativeVtableInstr(
    TypedReg Dst, TypedReg Recv, ushort VtableSlot, List<TypedReg> Args
) : IrInstr;

/// Pin a `String`/`Array` to borrow its raw buffer for FFI use. `Dst` is an
/// opaque pinned-view value whose layout / lifetime rules land in C4.
public sealed record PinPtrInstr(TypedReg Dst, TypedReg Src) : IrInstr;

/// Release a pinned view created by `PinPtr`. No result.
public sealed record UnpinPtrInstr(TypedReg Pinned) : IrInstr;

// ── Terminators ──────────────────────────────────────────────────────────────

// ── Terminators ──────────────────────────────────────────────────────────

public abstract record IrTerminator;

public sealed record RetTerm(TypedReg? Reg)                                           : IrTerminator;
public sealed record BrTerm(string Label)                                             : IrTerminator;
public sealed record BrCondTerm(TypedReg Cond, string TrueLabel, string FalseLabel)   : IrTerminator;
/// Throw the value in Reg as an exception; handled by the function's exception table.
public sealed record ThrowTerm(TypedReg Reg)                                          : IrTerminator;
