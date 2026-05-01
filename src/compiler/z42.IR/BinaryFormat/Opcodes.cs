namespace Z42.IR.BinaryFormat;

/// <summary>Opcode bytes for zbc binary instruction stream.</summary>
public static class Opcodes
{
    // ── Constants (0x00–0x0F) ─────────────────────────────────────────────────
    public const byte ConstI    = 0x00;  // integer constant; type tag = i32/i64
    public const byte ConstF    = 0x01;  // float constant;   type tag = f64
    public const byte ConstBool = 0x02;
    public const byte ConstStr  = 0x03;
    public const byte ConstNull = 0x04;
    public const byte Copy      = 0x05;
    public const byte ConstChar = 0x08;

    // ── Arithmetic & Logic (0x10–0x1F) ────────────────────────────────────────
    public const byte Add    = 0x10;
    public const byte Sub    = 0x11;
    public const byte Mul    = 0x12;
    public const byte Div    = 0x13;
    public const byte Rem    = 0x14;
    public const byte Neg    = 0x15;
    public const byte And    = 0x16;
    public const byte Or     = 0x17;
    public const byte Not    = 0x18;
    public const byte BitAnd = 0x19;
    public const byte BitOr  = 0x1A;
    public const byte BitXor = 0x1B;
    public const byte BitNot = 0x1C;
    public const byte Shl    = 0x1D;
    public const byte Shr    = 0x1E;
    public const byte ToStr  = 0x1F;

    // ── Comparison (0x30–0x3F, result type always bool) ────────────────────────
    public const byte Eq = 0x30;
    public const byte Ne = 0x31;
    public const byte Lt = 0x32;
    public const byte Le = 0x33;
    public const byte Gt = 0x34;
    public const byte Ge = 0x35;

    // ── Control Flow / Terminators (0x40–0x4F) ────────────────────────────────
    public const byte Br     = 0x40;
    public const byte BrCond = 0x41;
    public const byte Ret    = 0x42;  // void return
    public const byte RetVal = 0x43;  // return with value
    public const byte Throw  = 0x44;

    // ── Calls (0x50–0x5F) ─────────────────────────────────────────────────────
    public const byte Call             = 0x50;  // static call
    public const byte Builtin          = 0x51;  // built-in function call
    public const byte VCall            = 0x52;  // virtual dispatch
    public const byte CallNative       = 0x53;  // native interop direct symbol call (C2)
    public const byte CallNativeVtable = 0x54;  // native type vtable indirect call  (C5)
    public const byte LoadFn           = 0x55;  // push a function reference onto the operand stack (L2 no-capture lambda)
    public const byte CallIndirect     = 0x56;  // call via a FuncRef-typed register (L2 no-capture lambda)

    // ── Fields (0x60–0x6F) ────────────────────────────────────────────────────
    public const byte FieldGet  = 0x60;
    public const byte FieldSet  = 0x61;
    public const byte StaticGet = 0x62;
    public const byte StaticSet = 0x63;

    // ── Objects (0x70–0x7F) ───────────────────────────────────────────────────
    public const byte ObjNew     = 0x70;
    public const byte IsInstance = 0x71;
    public const byte AsCast     = 0x72;

    // ── Arrays & Strings (0x80–0x8F) ──────────────────────────────────────────
    public const byte ArrayNew    = 0x80;
    public const byte ArrayNewLit = 0x81;
    public const byte ArrayGet    = 0x82;
    public const byte ArraySet    = 0x83;
    public const byte ArrayLen    = 0x84;
    public const byte StrConcat   = 0x85;

    // ── Pin / FFI Borrow (0x90–0x9F) ──────────────────────────────────────────
    public const byte PinPtr   = 0x90;  // pin a String/Array buffer for FFI borrow (C4)
    public const byte UnpinPtr = 0x91;  // release pinned view (C4)
}

/// <summary>Type tag byte embedded in each instruction header.</summary>
public static class TypeTags
{
    public const byte Unknown = 0x00;  // also used for void / untyped
    public const byte Bool    = 0x01;
    public const byte I8      = 0x02;
    public const byte I16     = 0x03;
    public const byte I32     = 0x04;
    public const byte I64     = 0x05;
    public const byte U8      = 0x06;
    public const byte U16     = 0x07;
    public const byte U32     = 0x08;
    public const byte U64     = 0x09;
    public const byte F32     = 0x0A;
    public const byte F64     = 0x0B;
    public const byte Char    = 0x0C;
    public const byte Str     = 0x0D;
    public const byte Object  = 0x20;
    public const byte Array   = 0x21;

    public static byte FromString(string type) => type.ToLowerInvariant() switch
    {
        "void"   => Unknown,
        "bool"   => Bool,
        "i8"     => I8,
        "i16"    => I16,
        "i32"    => I32,
        "i64"    => I64,
        "u8"     => U8,
        "u16"    => U16,
        "u32"    => U32,
        "u64"    => U64,
        "f32"    => F32,
        "f64"    => F64,
        "char"   => Char,
        "str"    => Str,
        "int"    => I32,
        "long"   => I64,
        "double" => F64,
        "float"  => F32,
        _        => Object,
    };

    public static string ToIrString(byte tag) => tag switch
    {
        Unknown => "void",
        Bool    => "bool",
        I8      => "i8",
        I16     => "i16",
        I32     => "i32",
        I64     => "i64",
        U8      => "u8",
        U16     => "u16",
        U32     => "u32",
        U64     => "u64",
        F32     => "f32",
        F64     => "f64",
        Char    => "char",
        Str     => "str",
        Object  => "object",
        Array   => "array",
        _       => $"?0x{tag:X2}",
    };
}

/// <summary>ExecMode byte stored in each function header.</summary>
public static class ExecModes
{
    public const byte Interp = 0;
    public const byte Jit    = 1;
    public const byte Aot    = 2;

    public static byte FromString(string mode) => mode.ToLowerInvariant() switch
    {
        "jit" => Jit,
        "aot" => Aot,
        _     => Interp,
    };

    public static string ToIrString(byte mode) => mode switch
    {
        Jit => "Jit",
        Aot => "Aot",
        _   => "Interp",
    };
}

/// <summary>
/// Flags stored in the zbc file header at bytes[8..9].
/// bit 0 STRIPPED: metadata (SIGS/EXPT/IMPT) moved to zpkg; file requires zpkg index.
/// bit 1 HAS_DEBUG: file contains a DBUG section with source-map information.
/// </summary>
[Flags]
public enum ZbcFlags : ushort
{
    None     = 0x00,
    Stripped = 0x01,
    HasDebug = 0x02,
}

/// <summary>
/// Section tag constants (4-byte ASCII).
///
/// Section layout in a zbc file (all modes):
///   NSPC  — namespace string; always first (fixed position for fast scan)
///   STRS  — full string heap (full mode only)
///   TYPE  — class descriptors (full mode only)
///   SIGS  — function signature table (full mode only)
///   IMPT  — import table (full mode only)
///   EXPT  — export table (full mode only)
///   FUNC  — function bodies, indexed by position (both modes)
///   BSTR  — body-only string heap (stripped mode only, replaces STRS)
///   DBUG  — debug info / source maps (optional, flags.HAS_DEBUG=1)
/// </summary>
public static class SectionTags
{
    // ── Present in both modes ─────────────────────────────────────────────────
    public static readonly byte[] Nspc = "NSPC"u8.ToArray();  // namespace (always first)
    public static readonly byte[] Func = "FUNC"u8.ToArray();  // function bodies

    // ── Full mode only ────────────────────────────────────────────────────────
    public static readonly byte[] Strs = "STRS"u8.ToArray();  // string heap
    public static readonly byte[] Type = "TYPE"u8.ToArray();  // class descriptors
    public static readonly byte[] Sigs = "SIGS"u8.ToArray();  // function signatures
    public static readonly byte[] Impt = "IMPT"u8.ToArray();  // import table
    public static readonly byte[] Expt = "EXPT"u8.ToArray();  // export table

    // ── Stripped mode only ────────────────────────────────────────────────────
    public static readonly byte[] Bstr = "BSTR"u8.ToArray();  // body string heap

    // ── Optional ─────────────────────────────────────────────────────────────
    public static readonly byte[] Dbug = "DBUG"u8.ToArray();  // debug info
    public static readonly byte[] Tidx = "TIDX"u8.ToArray();  // R1: compile-time test metadata

    public static bool Equals(ReadOnlySpan<byte> a, byte[] b) =>
        a.SequenceEqual(b);
}
