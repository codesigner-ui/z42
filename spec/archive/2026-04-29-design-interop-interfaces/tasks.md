# Tasks: Design Interop Interfaces (C1)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29

## 进度概览

- [x] 阶段 1: Workspace 转换
- [x] 阶段 2: C ABI 头文件
- [x] 阶段 3: z42-abi crate
- [x] 阶段 4: z42-rs crate
- [x] 阶段 5: z42-macros crate
- [x] 阶段 6: Manifest JSON Schema
- [x] 阶段 7: IR opcode 声明（C# + Rust）
- [x] 阶段 8: 错误码注册
- [x] 阶段 9: 测试
- [x] 阶段 10: 文档同步
- [x] 阶段 11: 全绿验证

---

## 阶段 1: Workspace 转换

- [x] 1.1 修改 `src/runtime/Cargo.toml`：在 `[package]` 之上加 `[workspace] members = [".", "crates/z42-abi", "crates/z42-rs", "crates/z42-macros"]`
- [x] 1.2 验证 `cargo build --manifest-path src/runtime/Cargo.toml` 仍编译通过（无新 crate 时）
- [x] 1.3 `Cargo.lock` 自动重生（不手工编辑）

## 阶段 2: C ABI 头文件

- [x] 2.1 创建 `src/runtime/include/z42_abi.h`，含：
  - 文件头 license / version comment
  - `extern "C"` guard
  - `Z42TypeFlags` / `Z42MethodFlags` / `Z42FieldFlags` enum-style 常量
  - `Z42TypeDescriptor_v1` struct（字段顺序按 design §Decision 3）
  - `Z42MethodDesc` / `Z42FieldDesc` / `Z42TraitImpl` / `Z42MethodImpl` struct
  - `Z42TypeRef` / `Z42Value` / `Z42Args` / `Z42Error` opaque/transparent 类型
  - `z42_register_type` / `z42_invoke` / `z42_invoke_method` / `z42_resolve_type` / `z42_last_error` 函数声明
- [x] 2.2 用 `cc -x c -fsyntax-only src/runtime/include/z42_abi.h` 验证编译通过
- [x] 2.3 确认在 C++ 上下文也能 include（`#ifdef __cplusplus extern "C" { ... } #endif`）

## 阶段 3: z42-abi crate（Rust 镜像）

- [x] 3.1 创建 `src/runtime/crates/z42-abi/Cargo.toml`（package name `z42-abi`、`edition = "2021"`、空 dependencies）
- [x] 3.2 创建 `src/runtime/crates/z42-abi/src/lib.rs`：
  - `#![no_std]`（如能；否则留 `#![cfg_attr(not(test), no_std)]`）
  - `#[repr(C)]` 镜像所有 struct
  - `extern "C"` block 镜像所有 `z42_*` 函数声明
  - `pub use` 重导出
- [x] 3.3 创建 `src/runtime/crates/z42-abi/tests/abi_layout_tests.rs`：
  - 用 `core::mem::offset_of!` 验证 `abi_version` offset = 0、`flags` offset = 4
  - 验证 `size_of::<Z42TypeDescriptor_v1>` 与手算一致（写在测试中 `assert_eq!`）
- [x] 3.4 创建 `src/runtime/crates/z42-abi/README.md`
- [x] 3.5 `cargo build -p z42-abi` 通过

## 阶段 4: z42-rs crate（用户 trait/type 骨架）

- [x] 4.1 创建 `src/runtime/crates/z42-rs/Cargo.toml`（dependencies: `z42-abi = { path = "../z42-abi" }`）
- [x] 4.2 创建 `src/runtime/crates/z42-rs/src/lib.rs`：crate 入口 + re-export
- [x] 4.3 创建 `src/runtime/crates/z42-rs/src/types.rs`：`Z42Value`、`Z42Args`、`Z42TypeRef`、`Z42Error` 用户友好封装（内部包装 z42-abi 类型）
- [x] 4.4 创建 `src/runtime/crates/z42-rs/src/traits.rs`：
  - `pub trait Z42Type` 骨架（`const DESCRIPTOR: ...`、associated type stub）
  - `pub trait Z42Traceable` 骨架（`fn trace(&self, visitor: &mut dyn Visitor)` stub）
- [x] 4.5 创建 `src/runtime/crates/z42-rs/tests/skeleton_tests.rs`：
  - 用户视角"假定义" 一个 type 实现 `Z42Type`（手动实现，不走 derive）→ 编译通过即可
- [x] 4.6 创建 `src/runtime/crates/z42-rs/README.md`
- [x] 4.7 `cargo build -p z42-rs` 通过

## 阶段 5: z42-macros crate（proc macro 入口）

- [x] 5.1 创建 `src/runtime/crates/z42-macros/Cargo.toml`：
  - `[lib] proc-macro = true`
  - dependencies: `proc-macro2`、`quote`、`syn = { version = "2", features = ["full"] }`
- [x] 5.2 创建 `src/runtime/crates/z42-macros/src/lib.rs`：
  - `#[proc_macro_derive(Z42Type, attributes(z42))] pub fn derive_z42_type(_: TokenStream) -> TokenStream`
  - `#[proc_macro_attribute] pub fn methods(_: TokenStream, _: TokenStream) -> TokenStream`
  - `#[proc_macro] pub fn module(_: TokenStream) -> TokenStream`
  - 三个全部生成 `compile_error!("... not yet implemented (spec C3)")`
- [x] 5.3 创建 `src/runtime/crates/z42-macros/README.md`
- [x] 5.4 `cargo build -p z42-macros` 通过

## 阶段 6: Manifest JSON Schema

- [x] 6.1 创建 `docs/design/manifest-schema.json`（Draft 2020-12）：
  - `$schema`、`$id`（`https://z42-lang.org/schemas/manifest-v1.json` 占位）
  - 根 object：`abi_version` (const 1)、`module`、`version` (semver 正则)、`library_name`、`types[]`
  - `types[]` items：`name`、`size`、`align`、`flags[]`、`fields[]`、`methods[]`、`trait_impls[]`
  - `additionalProperties: true`（前向兼容）
- [x] 6.2 创建 `tests/data/example-manifest.json`（仿照 `interop.md` §9 例子，必须可被 schema 校验通过）
- [x] 6.3 创建 `tests/data/invalid-manifest-missing-types.json`（缺 `types` 字段，必须校验失败）
- [x] 6.4 选择 schema 验证工具（候选：`ajv-cli` via npx；或 `jsonschema` Python；或写极简 Rust 测试用 `jsonschema` crate）。决定记入 design.md 末尾"实施记录"

## 阶段 7: IR opcode 声明

### 7.1 C# 端
- [x] 7.1.1 修改 `src/compiler/z42.IR/BinaryFormat/Opcodes.cs`：在 Calls 段加 `CallNative = 0x53`、`CallNativeVtable = 0x54`；新增 "Pin / FFI Borrow" 段加 `PinPtr = 0x90`、`UnpinPtr = 0x91`
- [x] 7.1.2 修改 `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs`：4 个新 opcode 写入分支（暂用占位 IR record；具体字段在 C2/C4 敲定）
- [x] 7.1.3 修改 `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs`：对应读取分支，构造对应 IR record
- [x] 7.1.4 添加 `src/compiler/z42.Tests/Codegen/NativeOpcodeRoundTripTests.cs`：4 个新 opcode round-trip 测试

### 7.2 Rust 端
- [x] 7.2.1 修改 `src/runtime/src/metadata/bytecode.rs::Instruction`：加 4 个新变体（字段最小集合，匹配 C# IR 形状）
- [x] 7.2.2 修改 zbc 反序列化：新 opcode byte 路由到对应变体
- [x] 7.2.3 修改 `src/runtime/src/interp/mod.rs`：4 个新变体在 dispatch match 中走 `Trap`，附 TODO 指向 C2/C4
- [x] 7.2.4 添加 `src/runtime/tests/golden/run/native_opcode_trap_test.rs`：手工构造 zbc，VM 执行返回 Trap

## 阶段 8: 错误码注册

- [x] 8.1 修改 `docs/design/error-codes.md`：注册 Z0905–Z0910，统一占位描述"Reserved by spec design-interop-interfaces; semantics defined by C2–C5"
- [x] 8.2 验证编号无冲突（grep `Z0905|Z0906|Z0907|Z0908|Z0909|Z0910` 全仓）

## 阶段 9: 测试

- [x] 9.1 `cargo build --workspace --manifest-path src/runtime/Cargo.toml` 全绿
- [x] 9.2 `cargo test --workspace --manifest-path src/runtime/Cargo.toml` 全绿
- [x] 9.3 `dotnet build src/compiler/z42.slnx` 全绿
- [x] 9.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 9.5 `./scripts/test-vm.sh` 全绿
- [x] 9.6 Manifest schema 校验：`example-manifest.json` 通过、`invalid-manifest-*.json` 失败

## 阶段 10: 文档同步

- [x] 10.1 修改 `docs/design/interop.md` §10：Roadmap 表加 "C1: 接口骨架" 行（标注完成日期 = 归档日期）
- [x] 10.2 修改 `docs/design/interop.md` §9：在 manifest 段添加链接到 `manifest-schema.json`
- [x] 10.3 修改 `docs/design/ir.md`：加 4 个新 opcode 描述（操作数 + 语义占位 + 字节值）
- [x] 10.4 创建 `src/runtime/crates/README.md`：第 3 层目录 README，列出 3 个子 crate 职责
- [x] 10.5 修改 `src/runtime/README.md`：顶层 README 增加 `crates/` 子目录说明
- [x] 10.6 修改 `docs/roadmap.md`：Pipeline 实现进度表标注 C1 完成
- [x] 10.7 检查所有 README 同步规则（按 code-organization.md）

## 阶段 11: 全绿验证 + 归档准备

- [x] 11.1 输出阶段 8 验证报告
- [x] 11.2 spec scenarios 逐条对照实现位置
- [x] 11.3 检查 Scope 表与实际改动文件 1:1 对齐（无 Scope 外文件被改）
- [x] 11.4 等待归档触发

## 备注

- workspace 转换的 `Cargo.lock` 变更行数较大但全是 cargo 自动产物，不必人工 review
- 4 个新 opcode 的 IR record 字段在本 spec 用最小集合（够 round-trip + Trap 即可）；C2/C4 实施时再扩展
- 若阶段 7 发现 C# IR 节点与 Rust Instruction 难以最小集合对齐，停下回到 design.md 决策第 4/5 节调整
