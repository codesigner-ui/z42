using System.Text.Json.Serialization;

namespace Z42.IR;

// ── IR type system ───────────────────────────────────────────────────────────

/// Runtime type tag for each register value. Mapped from Z42Type during codegen.
[JsonConverter(typeof(JsonStringEnumConverter))]
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

/// Root bytecode module — matches the Rust `bytecode::Module` JSON schema.
public sealed record IrModule(
    string Name,
    IReadOnlyList<string> StringPool,
    List<IrClassDesc> Classes,
    List<IrFunction> Functions);

// ── Class descriptor ──────────────────────────────────────────────────────────

public sealed record IrClassDesc(
    string Name,
    [property: JsonPropertyName("base_class")] string? BaseClass,
    List<IrFieldDesc> Fields);
public sealed record IrFieldDesc(string Name, string Type);

// ── Function ──────────────────────────────────────────────────────────────────

public sealed record IrFunction(
    string Name,
    int ParamCount,
    string RetType,
    string ExecMode,
    List<IrBlock> Blocks,
    List<IrExceptionEntry>? ExceptionTable = null,
    [property: JsonPropertyName("is_static")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool IsStatic = false,
    /// <summary>
    /// Total number of registers used by this function (size for Vec pre-allocation in the VM).
    /// 0 means unknown (VM falls back to dynamic resizing).
    /// </summary>
    [property: JsonPropertyName("max_reg")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int MaxReg = 0,
    /// <summary>
    /// Source-line mapping table. Each entry records the source line number
    /// at a given (block, instruction) position. Only emits an entry when
    /// the line changes (run-length encoded). Used by the VM to show source
    /// locations in error messages and stack traces.
    /// </summary>
    [property: JsonPropertyName("line_table")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<IrLineEntry>? LineTable = null);

/// An entry in a function's line number table.
/// "From (BlockIdx, InstrIdx) onward, the source line is Line in File."
public sealed record IrLineEntry(
    [property: JsonPropertyName("block")] int BlockIdx,
    [property: JsonPropertyName("instr")] int InstrIdx,
    [property: JsonPropertyName("line")]  int Line,
    [property: JsonPropertyName("file")]  string? File = null);

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

/// Allocate a new object of ClassName, calling its constructor with Args.
public sealed record ObjNewInstr(TypedReg Dst, string ClassName, List<TypedReg> Args) : IrInstr;
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

// ── Terminators ──────────────────────────────────────────────────────────────

// ── Terminators ──────────────────────────────────────────────────────────

public abstract record IrTerminator;

public sealed record RetTerm(TypedReg? Reg)                                           : IrTerminator;
public sealed record BrTerm(string Label)                                             : IrTerminator;
public sealed record BrCondTerm(TypedReg Cond, string TrueLabel, string FalseLabel)   : IrTerminator;
/// Throw the value in Reg as an exception; handled by the function's exception table.
public sealed record ThrowTerm(TypedReg Reg)                                          : IrTerminator;
