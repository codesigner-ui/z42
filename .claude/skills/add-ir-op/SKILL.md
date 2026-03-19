---
name: add-ir-op
description: 向 z42 IR 添加新的指令（opcode）。在用户说"新增 IR 指令"、"加操作码"、"实现 xxx 指令" 时触发。
user-invocable: true
allowed-tools: Read, Edit, Grep
argument-hint: <instruction-name>
---

# 添加 IR 指令：$ARGUMENTS

新增一条 IR 指令需要同步修改三处，**缺一不可**：

## 步骤 1 — bytecode.rs（定义）

在 [bytecode.rs](src/runtime/src/bytecode.rs) 的 `Instruction` 枚举中追加 variant：

```rust
// 格式：指令名(目标寄存器, 操作数...)
NewOp(Reg, Reg, Reg),
```

操作数类型：
- `Reg` = `u32`（寄存器 ID）
- `u32` = 字符串池索引、类型 ID 等整数立即数
- `i32 / i64 / f64 / bool` = 常量立即值

## 步骤 2 — interp.rs（执行语义）

在 [interp.rs](src/runtime/src/interp.rs) 的 `exec_instr` match 中添加分支：

```rust
Instruction::NewOp(dst, a, b) => {
    // 实现执行语义，结果写入 frame.set(*dst, value)
}
```

**注意**：`exec_instr` 的 match 不允许有 `_` 通配兜底，每条指令必须显式处理。

## 步骤 3 — docs/design/ir.md（文档）

在 [ir.md](docs/design/ir.md) 的对应区域添加文档：

```
%r = new_op <type> %a, %b    # 一行描述语义
```

## 步骤 4 — 验证

```bash
cargo build --manifest-path src/runtime/Cargo.toml
```
