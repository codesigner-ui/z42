using System.Text.Json.Serialization;

namespace Z42.IR;

// ── Module ────────────────────────────────────────────────────────────────────

/// Root bytecode module — matches the Rust `bytecode::Module` JSON schema.
public sealed record IrModule(
    string Name,
    List<string> StringPool,
    List<IrFunction> Functions);

// ── Function ──────────────────────────────────────────────────────────────────

public sealed record IrFunction(
    string Name,
    int ParamCount,
    string RetType,
    string ExecMode,
    List<IrBlock> Blocks);

// ── Basic block ───────────────────────────────────────────────────────────────

public sealed record IrBlock(
    string Label,
    List<IrInstr> Instructions,
    IrTerminator Terminator);

// ── Instructions (discriminated union via JsonPolymorphic) ─────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ConstStrInstr),  "const_str")]
[JsonDerivedType(typeof(ConstI32Instr),  "const_i32")]
[JsonDerivedType(typeof(ConstI64Instr),  "const_i64")]
[JsonDerivedType(typeof(ConstF64Instr),  "const_f64")]
[JsonDerivedType(typeof(ConstBoolInstr), "const_bool")]
[JsonDerivedType(typeof(StrConcatInstr), "str_concat")]
[JsonDerivedType(typeof(ToStrInstr),     "to_str")]
[JsonDerivedType(typeof(CallInstr),      "call")]
[JsonDerivedType(typeof(BuiltinInstr),   "builtin")]
[JsonDerivedType(typeof(AddInstr),       "add")]
[JsonDerivedType(typeof(SubInstr),       "sub")]
[JsonDerivedType(typeof(MulInstr),       "mul")]
[JsonDerivedType(typeof(DivInstr),       "div")]
[JsonDerivedType(typeof(RemInstr),       "rem")]
[JsonDerivedType(typeof(EqInstr),        "eq")]
[JsonDerivedType(typeof(NeInstr),        "ne")]
[JsonDerivedType(typeof(LtInstr),        "lt")]
[JsonDerivedType(typeof(LeInstr),        "le")]
public abstract record IrInstr;

public sealed record ConstStrInstr(int Dst, int Idx)         : IrInstr;
public sealed record ConstI32Instr(int Dst, int Val)         : IrInstr;
public sealed record ConstI64Instr(int Dst, long Val)        : IrInstr;
public sealed record ConstF64Instr(int Dst, double Val)      : IrInstr;
public sealed record ConstBoolInstr(int Dst, bool Val)       : IrInstr;
public sealed record StrConcatInstr(int Dst, int A, int B)   : IrInstr;
public sealed record ToStrInstr(int Dst, int Src)            : IrInstr;
public sealed record CallInstr(int Dst, string Func, List<int> Args) : IrInstr;
public sealed record BuiltinInstr(int Dst, string Name, List<int> Args) : IrInstr;
public sealed record AddInstr(int Dst, int A, int B)         : IrInstr;
public sealed record SubInstr(int Dst, int A, int B)         : IrInstr;
public sealed record MulInstr(int Dst, int A, int B)         : IrInstr;
public sealed record DivInstr(int Dst, int A, int B)         : IrInstr;
public sealed record RemInstr(int Dst, int A, int B)         : IrInstr;
public sealed record EqInstr(int Dst, int A, int B)          : IrInstr;
public sealed record NeInstr(int Dst, int A, int B)          : IrInstr;
public sealed record LtInstr(int Dst, int A, int B)          : IrInstr;
public sealed record LeInstr(int Dst, int A, int B)          : IrInstr;

// ── Terminators ───────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(RetTerm),    "ret")]
[JsonDerivedType(typeof(BrTerm),     "br")]
[JsonDerivedType(typeof(BrCondTerm), "br_cond")]
public abstract record IrTerminator;

public sealed record RetTerm(int? Reg)                                       : IrTerminator;
public sealed record BrTerm(string Label)                                    : IrTerminator;
public sealed record BrCondTerm(int Cond, string TrueLabel, string FalseLabel) : IrTerminator;
