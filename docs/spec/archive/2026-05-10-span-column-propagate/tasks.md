# Tasks: Propagate Span Column Through IR / zbc / Runtime

> 状态：🟢 已完成 | 完成：2026-05-10 | 创建：2026-05-10
> 类型：feat + format change（最小化模式）

**变更说明：** Span.Column 已在 C# 编译器内的 Diagnostic 用得很好（pretty 错误渲染的 `--> file:line:col` + caret 对齐），但 IR LineEntry / zbc DBUG / 运行时 LineEntry 全部仅承载 line。本批让 column 一路透传到 zbc + runtime，让 stack trace 输出 `(file:line:col)` 替代 `(file:line)`，同时为未来 IDE / debugger 集成铺路。

**zbc 格式变更：** 每个 line table entry 增加 4 bytes（u32 column）。bump 次版本 1.0 → 1.1。所有现有 .zbc 必须重新生成（`scripts/regen-golden-tests.sh + build-stdlib.sh`），符合 [`workflow.md` "不为旧版本提供兼容"](../../.claude/rules/workflow.md) 原则。

## 阶段 1: 编译器侧 IR + emit

- [x] 1.1 [src/compiler/z42.IR/IrModule.cs](../../src/compiler/z42.IR/IrModule.cs) `IrLineEntry` 加 `Column` 字段（int，0 = unknown）
- [x] 1.2 [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs) `_lineTable.Add` 站点改取 `span.Column`

## 阶段 2: zbc 格式 bump

- [x] 2.1 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) `VersionMajor` / minor 序写：bump 写入 minor=1
- [x] 2.2 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) FUNC 段 line table 写入多 4 bytes column（u32）
- [x] 2.3 [src/runtime/src/metadata/formats.rs](../../src/runtime/src/metadata/formats.rs) `ZBC_VERSION` const bump 0.9 → 1.1（注：源里是 `[u16; 2] = [0, 9]` — 实际 major.minor 表达，需澄清）
- [x] 2.4 [src/runtime/src/metadata/zbc_reader.rs](../../src/runtime/src/metadata/zbc_reader.rs) line table 解码多读 u32 column；reject minor < 1（regen 引导）
- [x] 2.5 [src/runtime/src/metadata/bytecode.rs](../../src/runtime/src/metadata/bytecode.rs) `LineEntry` 加 `column: u32` 字段（serde default = 0 兼容空 column）

## 阶段 3: 运行时 + 渲染

- [x] 3.1 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `resolve_line` 改返回 `(line, column)` tuple；call sites 更新（interp Throw / exec_instr.update_caller_line / jit translate.rs 4 处）
- [x] 3.2 [src/runtime/src/exception/mod.rs](../../src/runtime/src/exception/mod.rs) `FrameInfo.column: Cell<u32>` 加上；`update_top_frame_line` 改 `update_top_frame_pos(line, col)`；`format_stack_trace` 输出 `(file:line:col)` 当 col > 0
- [x] 3.3 JIT helpers (call/vcall/call_indirect/throw) 改 `caller_line: u32, caller_col: u32`；签名 +1 i32 arg
- [x] 3.4 translate.rs emit 多传 column iconst 常量
- [x] 3.5 cranelift sigs 加 i32 column

## 阶段 4: 测试 + 文档

- [x] 4.1 既有 stack_trace_field 测试不需改（断言用 `.Contains("Demo.Inner")` 不依赖具体格式）
- [x] 4.2 加 1 个 unit 测试 `format_stack_trace` 支持 column 显示
- [x] 4.3 [docs/design/runtime/vm-architecture.md](../../docs/design/runtime/vm-architecture.md) 简短记录 column 字段
- [x] 4.4 regen-golden + build-stdlib 全量重生

## 阶段 5: 验证

- [x] 5.1 dotnet build + dotnet test 全绿
- [x] 5.2 cargo build + cargo test --lib 全绿
- [x] 5.3 test-vm.sh interp+jit 全绿
- [x] 5.4 手动 smoke：观察 stack trace 输出是否带 column

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | IrLineEntry 加 Column |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` | MODIFY | 透传 Span.Column |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 写 column + bump version |
| `src/runtime/src/metadata/formats.rs` | MODIFY | ZBC_VERSION bump |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 读 column + minor check |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | LineEntry.column |
| `src/runtime/src/interp/mod.rs` | MODIFY | resolve_line 返回 col；Throw 写 col |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | update_caller_line/col |
| `src/runtime/src/exception/mod.rs` | MODIFY | FrameInfo.column + format_stack_trace |
| `src/runtime/src/jit/helpers/{call,vcall,closure,control,registry}.rs` | MODIFY | 4 helper 签名 + caller_col |
| `src/runtime/src/jit/translate.rs` | MODIFY | emit column const |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 文档同步 |

## 备注

- `ZBC_VERSION` 在 formats.rs 是 `[0, 9]` —— 看起来是 [major, minor] 但写为 0.9。需要弄清写入的是 major.minor 还是 single number；阶段 2 实施时确认。
- 老 zbc 拒绝读取 → 错误信息提示 regen 命令（与现有 0→1 路径风格一致）。
- column 值是 1-based（与 Span.Column 一致）；输出时直接显示。
