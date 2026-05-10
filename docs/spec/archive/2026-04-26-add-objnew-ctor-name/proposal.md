# Proposal: ObjNewInstr 携带 ctor 名 — 支持 ctor 重载

## Why

Wave 2 实施时发现 VM 限制：用户代码 `new Exception(msg, inner)` 双 ctor
重载无法工作。stdlib 编译器对重载方法 emit `MethodName$N` arity suffix
（已有惯例），所以双 ctor 的 Exception 产出 `Std.Exception.Exception$1` /
`$2`；但 VM `ObjNew` 不携带具体 ctor 名，仅按 `${class}.${simple}` 查找
（`Std.Exception.Exception` 无 suffix），找不到任何重载。

**根因定位**（按 [.claude/rules/workflow.md "修复必须从根因出发"](.claude/rules/workflow.md#修复必须从根因出发-2026-04-26-强化)）：

1. Call 指令历史上**编译期完整完成 overload resolution**，IR 直接携带具体
   FQ 函数名（含 `$N`）。VM 只查表，不做 dispatch。
2. ObjNew 指令**未做同等处理** — IR 仅携带 ClassName，VM 推断
   `${class}.${simple}` 作为 ctor。这是基于历史"ctor 只有一个"假设的简化。
3. 当 ctor 重载出现，假设破裂；VM 推断的固定字符串无法对应多个 overload。

按 z42 总体设计原则（lang/runtime-rust.md 等），**dispatch 必须在编译期完
成，VM 只做查表**。Call 指令已遵守此原则，ObjNew 应当对齐。

按 workflow 根因修复原则，**禁止在 VM 加 fallback `${class}.${simple}$${args.len}` 这类
"VM 推断 overload"补丁** —— 那把 overload resolution 半推到运行时，违反整体
设计；并且当 default param / params 进入图景时（同一 ctor 多 arity） ambiguity
进一步加剧。

## What Changes

让 `ObjNewInstr` 与 `Call` 指令**对齐**：携带具体 ctor 名（含 `$N` suffix
如有），VM 直查 `function_table[ctor_name]`，不再推断。

### IR 改动

```csharp
// C# (z42.IR/IrModule.cs)
旧：record ObjNewInstr(TypedReg Dst, string ClassName, List<TypedReg> Args)
新：record ObjNewInstr(TypedReg Dst, string ClassName, string CtorName, List<TypedReg> Args)

// Rust (runtime/src/metadata/bytecode.rs)
旧：Instruction::ObjNew { dst, class_name, args }
新：Instruction::ObjNew { dst, class_name, ctor_name, args }
```

### zbc 编/解码改动

OP_OBJ_NEW 字段后增加 ctor_name 字符串池索引（`u32`）。

### 编译器 ctor name 选择

`TypeChecker.Exprs.cs:160` `BoundNew` 数据结构增加 `CtorName` 字段；TypeCheck
阶段做 ctor overload resolution，按 args 数量从 ClassType 的 Methods 字典里
找匹配的 ctor name（含 `$N`）。Codegen 用此名传给 `ObjNewInstr`。

### VM 改动

`exec_instr.rs ObjNew`：直接用 `ctor_name` 查 `module.func_index` /
lazy_loader；删除 `${class}.${simple}` 推断 fallback。

### zbc 版本 bump

`ZBC_VERSION: [u16; 2]` 从 `[0, 4]` → `[0, 5]`。

旧 zbc（0.4 及以下）解码时 ctor_name 字段不存在 → 兼容路径填
`${class}.${simple}` 作为旧 ObjNew 行为的等价。

## Scope（允许改动的文件/模块）

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | edit | `ObjNewInstr` 加 `CtorName` 字段 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | edit | 编码 ctor_name pool idx |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | edit | 解码 ctor_name pool idx；旧版 fallback |
| `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs` | edit | 显示 CtorName（与 simple 不同时） |
| `src/compiler/z42.IR/IrVerifier.cs` | edit (minimal) | 若有验证 ClassName，对应处理 CtorName |
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | edit | `BoundNew` 加 `CtorName` 字段 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | edit | NewExpr 处理时做 ctor overload resolution |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | edit | EmitBoundNew 用 BoundNew.CtorName 传 ObjNewInstr |
| `src/runtime/src/metadata/bytecode.rs` | edit | `Instruction::ObjNew` 加 `ctor_name` 字段 |
| `src/runtime/src/metadata/binary.rs` | edit | OP_OBJ_NEW 解码 ctor_name；旧版 fallback |
| `src/runtime/src/metadata/zbc_reader.rs` | edit | 同上 |
| `src/runtime/src/metadata/formats.rs` | edit | `ZBC_VERSION` 0.4 → 0.5 |
| `src/runtime/src/interp/exec_instr.rs` | edit | ObjNew 用 ctor_name 直查；删 simple 推断 |
| `src/runtime/src/jit/translate.rs` 等 | edit (若涉及) | JIT 路径同步用 ctor_name |
| `src/runtime/tests/golden/run/` | regenerate + add | 重生成所有 source.zbc；新增 ctor 重载 golden |
| `docs/design/ir.md` | edit | 文档 ObjNewInstr 新签名；zbc 版本 bump 记录 |
| `docs/design/vm-architecture.md` | edit | 在 VCall 分发章节附近加 ObjNew dispatch 说明 |

## Out of Scope

- ctor 重载选择失败的诊断错误码细化（保留现有 TypeChecker 错误处理）
- default param ctor / params ctor 的复杂选择（沿用现有 method overload 选择算法，不专门为 ctor 优化）
- VM 端 `${class}.${simple}` 推断**完全删除**（保留作 0.4 zbc 兼容路径，
  loader 解码时用此填充 ctor_name；exec 路径不再"推断"）
- ImportedSymbolLoader 对 ctor 重载的 TSIG 表达 — 应该已经支持（method 名带 `$N`）

## Open Questions

- [x] `BoundNew` 是否新建 record 还是改现有 — **改现有**（增字段，向后兼容）
- [x] zbc 旧版兼容路径 — **保留**：解码 0.4 zbc 时 ctor_name 填 `${class}.${simple}`
- [x] VM 端 simple 推断 — **删 exec 路径推断**，loader 解码兼容路径保留

## Blocks / Unblocks

- **Unblocks**：
  - Wave 2 限制 #2 解锁
  - Exception 双 ctor 重新启用：`Exception(msg)` / `Exception(msg, inner)`
    都可用
  - 任何用户类 / stdlib 类的 ctor 重载
- **Blocks**：无（独立改动）
