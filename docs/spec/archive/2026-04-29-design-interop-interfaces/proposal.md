# Proposal: Design Interop Interfaces (Tier 1/2/3 Scaffold)

## Why

`docs/design/interop.md` 定义的三层 ABI 横跨 7 个实施 milestone（L2.M8–M14）。如果 7 个 spec 各自独立设计接口，**形状会在实施过程中漂移** —— 后一个 spec 实现时常发现前一个 spec 锁死的 trait 签名不够用，被迫返工或加兼容层。

按"接口先行"原则，本变更**一次性把所有公开接口（C ABI 头文件、Rust crate trait/类型签名、manifest JSON Schema、IR opcode 声明、错误码）钉死**，只交付编译通过的桩（`unimplemented!()`/`Trap`）。后续 C2–C5 spec 在稳定接口下填实现。

**本变更不引入运行时行为变更**：所有新增 IR opcode 在 VM 里直接 `Trap`；所有 macro 在展开时报"NotYetImplemented"；C 头函数在 VM 里返回 `Z42Error::NotImplemented`。

## What Changes

- **C ABI 头文件**：新建 `src/runtime/include/z42_abi.h`，包含 `Z42TypeDescriptor_v1`、`Z42MethodDesc`、`Z42FieldDesc`、`Z42TraitImpl`、所有 `z42_*` 函数声明
- **Rust workspace 转换**：`src/runtime/Cargo.toml` 改为 workspace + 根 package 双重身份（root package as workspace member）
- **Crate `z42-abi`**：Tier 1 类型的 Rust 镜像（`#[repr(C)]`，`no_std`-friendly）
- **Crate `z42-rs`**：用户面向类型 / trait 骨架（`Z42Type`、`Z42Traceable`、`Z42Args`、`Z42Value`、`Z42TypeRef`、`Z42Error`）
- **Crate `z42-macros`**：proc macro 入口签名（`Z42Type` derive、`methods` attr、`module!` macro），实现体 `unimplemented!()`
- **Manifest JSON Schema**：`docs/design/manifest-schema.json`（Draft 2020-12）描述 `.z42abi` 格式 v1
- **IR opcode 声明**（C# + Rust）：`CallNative`、`CallNativeVtable`、`PinPtr`、`UnpinPtr` 四个新指令；只新增枚举值，dispatch 走 `Trap`
- **错误码**：`Z0905`–`Z0910` 注册到 `docs/design/error-codes.md`（具体语义留给 C2–C5）
- **测试**：crate 编译 + JSON Schema 自校验 + IR opcode 序列化往返
- **文档同步**：`docs/design/interop.md` §10 路线图加 C1 条目；新建/更新 README.md

## Scope

**变更文件**（NEW / MODIFY 必须每条对应至少一个 task）：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/include/z42_abi.h` | NEW | Tier 1 C ABI 完整声明 |
| `src/runtime/Cargo.toml` | MODIFY | 加 `[workspace]` 段；保持 `[package]` 段不变 |
| `src/runtime/Cargo.lock` | MODIFY | workspace 转换后自动重生 |
| `src/runtime/crates/z42-abi/Cargo.toml` | NEW | abi crate manifest |
| `src/runtime/crates/z42-abi/src/lib.rs` | NEW | `#[repr(C)]` 镜像类型 |
| `src/runtime/crates/z42-rs/Cargo.toml` | NEW | rs crate manifest |
| `src/runtime/crates/z42-rs/src/lib.rs` | NEW | crate 入口 + re-exports |
| `src/runtime/crates/z42-rs/src/types.rs` | NEW | `Z42Value`、`Z42Args`、`Z42TypeRef`、`Z42Error` |
| `src/runtime/crates/z42-rs/src/traits.rs` | NEW | `Z42Type`、`Z42Traceable` trait 骨架 |
| `src/runtime/crates/z42-macros/Cargo.toml` | NEW | macros crate manifest（`proc-macro = true`） |
| `src/runtime/crates/z42-macros/src/lib.rs` | NEW | proc macro 入口 + `unimplemented!()` 桩 |
| `src/runtime/crates/README.md` | NEW | 第 3 层目录 README（按 code-organization 规则） |
| `src/runtime/crates/z42-abi/README.md` | NEW | crate README |
| `src/runtime/crates/z42-rs/README.md` | NEW | crate README |
| `src/runtime/crates/z42-macros/README.md` | NEW | crate README |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `Instruction` enum 加 4 个新变体；序列化往返 |
| `src/runtime/src/interp/mod.rs` | MODIFY | 新 opcode 在 dispatch 中走 `Trap`，附 TODO 注释指向 C2 |
| `src/runtime/src/lib.rs` | MODIFY | 暴露 abi crate 给 VM 使用（路径依赖） |
| `src/runtime/README.md` | MODIFY | 顶层 README 增加 crates/ 子目录说明 |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 加 4 个新 opcode 常量 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | 4 个新 opcode 读取分支（暂返回桩 IR 节点） |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 4 个新 opcode 写入分支 |
| `docs/design/manifest-schema.json` | NEW | `.z42abi` v1 JSON Schema |
| `docs/design/error-codes.md` | MODIFY | 注册 Z0905–Z0910（语义占位） |
| `docs/design/interop.md` | MODIFY | §10 Roadmap 加 C1 行；§9 反向引用 manifest-schema.json |
| `docs/design/ir.md` | MODIFY | 加 4 个新 opcode 描述 |
| `docs/roadmap.md` | MODIFY | Pipeline 进度表更新 |
| `src/runtime/crates/z42-abi/tests/abi_layout_tests.rs` | NEW | 验证 `#[repr(C)]` 字段 offset / size |
| `src/runtime/crates/z42-rs/tests/skeleton_tests.rs` | NEW | 验证 trait/type 编译通过 |
| `src/runtime/tests/golden/run/native_opcode_trap_test.rs` | NEW | 调用 4 个新 opcode 时 VM 返回 Trap 而非崩溃 |
| `tests/manifest_schema_validation.test` | NEW | 用示例 manifest 校验 JSON Schema |

**只读引用**（理解上下文，不修改；不计入并行冲突）：

- `docs/design/interop.md` §1–§9（已存在的设计文档作为接口规范来源）
- `src/runtime/src/metadata/bytecode.rs` 现有 `Instruction` 变体（理解 enum 风格）
- `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` 现有 opcode 常量（避免冲突）
- `docs/design/error-codes.md` 现有编号（避免冲突）

## Out of Scope

- **任何运行时行为**：本 spec 不实现 `z42_register_type`、libffi 接入、TypeRegistry —— 那是 C2 的工作
- **proc macro 实现体**：`#[derive(Z42Type)]` 等 macro 在本 spec 只暴露入口签名，展开时 `unimplemented!()` —— C3 的工作
- **`pinned` 块语法**：parser/typechecker/IR codegen 改动不在本 spec —— C4 的工作
- **manifest 解析器实现**：本 spec 只定义 schema；reader 在 C5 实现
- **stdlib 迁移**：现有 L1 `[Native]` 机制保持不变；不动 `dispatch_table`
- **JIT/AOT 后端**：不碰

## Open Questions

- [ ] **Q1**：Rust workspace 转换是否影响现有 `cargo build --manifest-path src/runtime/Cargo.toml` 调用？
  - 倾向：保持向后兼容（root package + workspace 双身份）
- [ ] **Q2**：`src/runtime/crates/` 还是 `crates/`（顶层）？
  - 倾向：`src/runtime/crates/`，把 Rust 侧整体收口在 runtime 目录下
- [ ] **Q3**：4 个新 opcode 在序列化格式里占用哪些 byte 值？
  - 倾向：紧接当前最大值 +1, +2, +3, +4，design.md 中给出具体值
