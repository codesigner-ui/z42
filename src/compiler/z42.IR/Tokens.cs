namespace Z42.IR;

// ── Phase 3 (tokenize-ir-and-zbc-bump, 2026-05-09) ────────────────────────────
//
// Token newtypes that mirror Rust `runtime::metadata::tokens`. Each token is
// a `u32` newtype identifying a runtime entity (method / type / static-field /
// builtin). Persisted into zbc 1.0 IR fields directly (no string indirection).
//
// Token space layout (shared with Rust):
//   intra-module:    [0,             0x7FFF_FFFE]   (~2.1B capacity)
//   IMPORT_BASE:     0x8000_0000
//   import indices:  [0x8000_0000,   0xFFFF_FFFE]   (idx = token - IMPORT_BASE)
//   UNRESOLVED:      0xFFFF_FFFF
//
// Reserved high byte: when Phase 4+ (or B2 CLR-style metadata) needs to encode
// table tag `tableId << 24 | RID`, the high byte is available. Today it stays 0.

/// <summary>Import-kind tag — used as the first byte of an IMPT entry.
/// Named <c>ImportKind</c> rather than <c>TokenKind</c> to avoid collision with
/// <c>Z42.Syntax.Lexer.TokenKind</c>.</summary>
public enum ImportKind : byte
{
    Method      = 0x01,
    Type        = 0x02,
    StaticField = 0x03,
    Builtin     = 0x04, // closed set, never imported but tag for completeness
}

/// <summary>Shared token constants.</summary>
public static class TokenConsts
{
    public const uint Unresolved = 0xFFFF_FFFFu;
    public const uint ImportBase = 0x8000_0000u;
}

/// <summary>
/// Identifies one function in <c>Module.Functions: List&lt;IrFunction&gt;</c>
/// (per-module). Allocated by <c>TokenAllocator</c> in deterministic order.
/// Cross-zpkg references encoded as <c>ImportBase + idx</c> into IMPT.
/// </summary>
public readonly record struct MethodId(uint Value)
{
    public static MethodId Unresolved => new(TokenConsts.Unresolved);
    public bool IsResolved => Value != TokenConsts.Unresolved;
    public bool IsImport   => Value >= TokenConsts.ImportBase && Value != TokenConsts.Unresolved;
    public uint ImportIdx  => Value - TokenConsts.ImportBase;
    public override string ToString() =>
        Value == TokenConsts.Unresolved ? "MethodId(unresolved)"
        : IsImport ? $"MethodId(import {ImportIdx})"
        : $"MethodId({Value})";
}

/// <summary>
/// Identifies one class / type in <c>Module.Classes</c> (per-module).
/// Allocated by FQ class name dictionary order.
/// </summary>
public readonly record struct TypeId(uint Value)
{
    public static TypeId Unresolved => new(TokenConsts.Unresolved);
    public bool IsResolved => Value != TokenConsts.Unresolved;
    public bool IsImport   => Value >= TokenConsts.ImportBase && Value != TokenConsts.Unresolved;
    public uint ImportIdx  => Value - TokenConsts.ImportBase;
    public override string ToString() =>
        Value == TokenConsts.Unresolved ? "TypeId(unresolved)"
        : IsImport ? $"TypeId(import {ImportIdx})"
        : $"TypeId({Value})";
}

/// <summary>
/// Identifies one static field slot. Globally numbered across the VmContext;
/// allocated by (declaring class FQ, field name) dictionary order.
/// </summary>
public readonly record struct StaticFieldId(uint Value)
{
    public static StaticFieldId Unresolved => new(TokenConsts.Unresolved);
    public bool IsResolved => Value != TokenConsts.Unresolved;
    public bool IsImport   => Value >= TokenConsts.ImportBase && Value != TokenConsts.Unresolved;
    public uint ImportIdx  => Value - TokenConsts.ImportBase;
    public override string ToString() =>
        Value == TokenConsts.Unresolved ? "StaticFieldId(unresolved)"
        : IsImport ? $"StaticFieldId(import {ImportIdx})"
        : $"StaticFieldId({Value})";
}

/// <summary>
/// Identifies one builtin function in the runtime <c>BUILTINS</c> static table
/// (cross-module / per-process). Closed set — never imported. The
/// <see cref="IsImport"/> check still compiles but always returns false in
/// well-formed IR.
/// </summary>
public readonly record struct BuiltinId(uint Value)
{
    public static BuiltinId Unresolved => new(TokenConsts.Unresolved);
    public bool IsResolved => Value != TokenConsts.Unresolved;
    public bool IsImport   => Value >= TokenConsts.ImportBase && Value != TokenConsts.Unresolved;
    public uint ImportIdx  => Value - TokenConsts.ImportBase;
    public override string ToString() =>
        Value == TokenConsts.Unresolved ? "BuiltinId(unresolved)"
        : $"BuiltinId({Value})";
}
