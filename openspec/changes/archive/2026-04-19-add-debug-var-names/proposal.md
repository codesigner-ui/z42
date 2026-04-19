# Proposal: 调试变量名 + 调用栈

## Why

当前 VM 错误信息只有函数名和行号（`at Main (line 42)`），无法得知哪个变量出错，也没有调用栈链。用户遇到运行时错误时缺乏足够的调试信息。

## What Changes

1. 编译器生成 **局部变量名 → 寄存器** 映射表（`LocalVarTable`），写入 IrFunction
2. zbc 二进制格式在 DBUG section 中序列化变量名表
3. VM 读取变量名表，错误时输出变量名替代寄存器编号
4. VM 异常传播时构建 **调用栈链**，输出完整 stack trace

## Scope（允许改动的文件/模块）

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.IR/IrModule.cs` | 新增类型 | `IrLocalVarEntry` record + IrFunction 新增字段 |
| `z42.Semantics/Codegen/FunctionEmitter.cs` | 修改 | 生成 LocalVarTable |
| `z42.IR/BinaryFormat/ZbcWriter.cs` | 修改 | DBUG section 写入变量名表 |
| `z42.IR/BinaryFormat/ZbcReader.cs` | 修改 | DBUG section 读取变量名表 |
| `z42.IR/BinaryFormat/ZasmWriter.cs` | 修改 | disasm 输出变量名表 |
| `src/runtime/src/metadata/bytecode.rs` | 修改 | Function 新增 local_vars 字段 |
| `src/runtime/src/metadata/zbc_reader.rs` | 修改 | 读取 DBUG 变量名表 |
| `src/runtime/src/interp/mod.rs` | 修改 | 错误格式化 + 调用栈构建 |

## Out of Scope

- 变量作用域（scope range）映射 — 后续按需添加
- 交互式调试器 / breakpoint 支持
- JIT 侧的调试符号（仅 interpreter）
- 变量类型信息（TypedReg 的 IrType 已有，但不纳入本次变量名表）

## Open Questions

- (无)
