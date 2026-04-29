# Design: Extend `[Native]` for Tier 1 dispatch (C6)

## Architecture

```
Source: [Native(lib="numz42", type="Counter", entry="inc")]
        public static extern long CounterInc(long ptr);

Parser → AST:
  FunctionDecl(
      Name = "CounterInc",
      Modifiers = Static | Extern,
      NativeIntrinsic = null,                          ← legacy L1 path
      Tier1Binding = Tier1NativeBinding("numz42", "Counter", "inc"),
      ...)

TypeChecker:
  fn.IsExtern && (fn.NativeIntrinsic ⊕ fn.Tier1Binding)   // exactly one must be set

IR Codegen — EmitNativeStub:
  if fn.Tier1Binding is { Lib, TypeName, Entry }:
      emit `CallNativeInstr(dst, lib, type, entry, args)` directly into the
      stub function body.
  else if fn.NativeIntrinsic is "__name":
      emit `BuiltinInstr(dst, "__name", args)` (existing L1 path).
  else:
      diagnostic Z0903 (existing).

VM (already ready):
  CallNativeInstr → C2 registry lookup → libffi → marshal
  (BuiltinInstr   → existing L1 dispatch_table)
```

## Decisions

### Decision 1: Two parallel fields on FunctionDecl

Add `Tier1NativeBinding? Tier1Binding` alongside existing `string? NativeIntrinsic`. Mutually exclusive at parse time + validated again at type-check.

```csharp
public sealed record Tier1NativeBinding(string Lib, string TypeName, string Entry);

public sealed record FunctionDecl(
    string Name,
    List<Param> Params,
    TypeExpr ReturnType,
    BlockStmt Body,
    Visibility Visibility,
    FunctionModifiers Modifiers,
    string? NativeIntrinsic,
    Span Span,
    List<Expr>? BaseCtorArgs = null,
    List<string>? TypeParams = null,
    WhereClause? Where = null,
    Tier1NativeBinding? Tier1Binding = null);   // ← new
```

Default null preserves backward compat; existing constructors unchanged.

### Decision 2: Parser detects form via lookahead

Current `TryReadNativeIntrinsic` matches `[Native("..." )]` exactly. Extend to also match `[Native(K=V, K=V, K=V)]` form via key=string-literal tokens.

```csharp
public sealed record NativeAttribute(
    string? Intrinsic,
    Tier1NativeBinding? Tier1);

internal static NativeAttribute? TryReadNativeAttribute(ref TokenCursor cursor) {
    // detect `[Native(...)]`; dispatch on first arg form
    // - `"<string>"` only → return new NativeAttribute(intrinsic: <string>, null)
    // - `IDENT = "..."` ...form → parse k=v pairs (lib/type/entry), validate completeness, return new NativeAttribute(null, Tier1)
}
```

Diagnostic E0907 (`NativeAttributeMalformed`) when:
- 单 string literal 后跟 `=`（看起来像新形式但写法错）
- 新形式缺 `lib` / `type` / `entry` 任一
- 出现非法键名

### Decision 3: TypeChecker xor 校验

```csharp
bool hasNative = fn.NativeIntrinsic != null || fn.Tier1Binding != null;
if (fn.IsExtern && !hasNative)
    diag.Error(ExternRequiresNative, ...);
if (!fn.IsExtern && hasNative)
    diag.Error(NativeRequiresExtern, ...);
// (parser already enforces xor; defensive double check)
```

### Decision 4: IrGen 路径

`EmitNativeStub` 拆为两条：

```csharp
private static IrFunction EmitNativeStub(
    string qualifiedName, int totalParams, int paramOffset,
    string? intrinsicName,
    Tier1NativeBinding? tier1,
    bool isVoid)
{
    var args = ...;
    var dst = ...;
    IrInstr call = tier1 is { } t
        ? new CallNativeInstr(dst, t.Lib, t.TypeName, t.Entry, args)
        : new BuiltinInstr(dst, intrinsicName!, args);
    var instrs = new List<IrInstr> { call };
    var term = new RetTerm(isVoid ? null : dst);
    return new IrFunction(qualifiedName, totalParams, ..., [new IrBlock("entry", instrs, term)], ...);
}
```

调用方按 method/fn 各自选最合适的 binding。

### Decision 5: 错误码 E0907

C1 占位 Z0907 描述是 "NativeMethodSignatureMismatch"（manifest vs declaration mismatch）。本 spec 复用编号但表意调整为更通用的 "NativeAttributeMalformed"（仅在 attribute 解析失败时抛）。manifest-vs-decl 校验在 source generator 阶段（独立 spec）会有自己的 trigger，到时再细化。

## Implementation Notes

### Parser: 扩展 TryReadNativeIntrinsic 而非分裂

保留单一入口 `TryReadNativeAttribute`（原名重命名）减少调用方分支：

```csharp
internal static NativeAttribute? TryReadNativeAttribute(ref TokenCursor cursor)
{
    // 已有：`[Native(`
    // 1. 第一个非空 token 是 StringLiteral：旧形式
    // 2. 第一个非空 token 是 Identifier 后跟 `=`：新形式
    // 3. 都不是：返回 null（fallback skip-attribute 路径）
}
```

调用方只接 `NativeAttribute? attr`，把 `attr.Intrinsic` / `attr.Tier1` 透传给 `FunctionDecl` 构造器即可。

### 测试覆盖

| Test | 验证 |
|------|------|
| Parse_OldForm_Intrinsic | `[Native("__println")]` → fn.NativeIntrinsic == "__println"；Tier1Binding == null |
| Parse_NewForm_Tier1 | `[Native(lib="numz42", type="Counter", entry="inc")]` → Tier1Binding 各字段正确；NativeIntrinsic == null |
| Parse_NewForm_OutOfOrder | `[Native(entry="x", lib="y", type="z")]` 顺序无关也接受 |
| Parse_NewForm_MissingLib_E0907 | 缺 `lib=` 报 E0907 |
| TypeCheck_BothForms_Forbidden | （理论 parser 已防止；defensive 测试）|
| Codegen_Tier1_EmitsCallNative | 含 Tier1Binding 的 extern fn → IR 含 `CallNativeInstr` |
| Codegen_Legacy_EmitsBuiltin | 含 Intrinsic 的 extern fn → IR 含 `BuiltinInstr`（不回归）|

## Risk

- **风险**：Parser 重命名 `TryReadNativeIntrinsic` → `TryReadNativeAttribute` 影响多个调用方
  - 缓解：grep 全仓改名；同时保留旧名作 alias 不必要（无外部消费者）
- **回滚**：单 commit 整体 revert；旧 L1 路径行为不变即可恢复
