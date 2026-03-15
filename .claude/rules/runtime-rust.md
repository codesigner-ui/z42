---
paths:
  - "src/runtime/**/*.rs"
---

# Rust VM 开发规范

## 错误处理

- 所有可能失败的函数返回 `anyhow::Result<T>`
- 内部 VM 错误（非用户错误）用 `bail!("...")` 或 `anyhow::anyhow!(...)`
- 领域错误类型（如 `BytecodeError`）用 `thiserror::Error` 定义
- **禁止** `unwrap()` / `expect()` 在非测试代码中出现

## 指令集扩展

每次新增 `Instruction` variant，必须同时更新：
1. `bytecode.rs` — 枚举定义
2. `interp.rs` — `exec_instr` match 分支（不允许有 `_` 通配兜底）
3. `specs/ir.md` — 指令文档

## Value 类型

- `Value` 枚举是运行时动态类型，所有算术操作前必须匹配类型一致性
- 类型不匹配时 `bail!` 而不是静默转换

## 执行模式

- `ExecMode` 决定函数级别的分发路径
- 模块级默认模式 → `Vm::default_mode`；函数级注解优先
- JIT/AOT 后端在实现完成前**必须**返回 `bail!("... not yet implemented")`，不允许部分实现

## 序列化

- `Module`、`Function`、`Instruction` 等持久化类型必须 `#[derive(Serialize, Deserialize)]`
- 二进制格式使用 `bincode`；文本调试格式用 `serde_json`（可选依赖）
