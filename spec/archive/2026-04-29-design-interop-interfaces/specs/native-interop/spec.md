# Spec: Native Interop Scaffold

## ADDED Requirements

### Requirement: C ABI Header File

`src/runtime/include/z42_abi.h` 提供 Tier 1 ABI 的完整 C 声明，可作为 `#include` 入口被任意 C/C++ 项目引用。

#### Scenario: 头文件独立编译
- **WHEN** 用 `cc -x c -fsyntax-only src/runtime/include/z42_abi.h` 检查
- **THEN** 编译通过，无 warning

#### Scenario: 关键类型可访问
- **WHEN** 用户 C 代码 `#include "z42_abi.h"` 后引用 `Z42TypeDescriptor_v1`、`Z42MethodDesc`、`Z42FieldDesc`、`Z42TraitImpl`、`z42_register_type`、`z42_invoke`、`z42_resolve_type`、`z42_last_error`
- **THEN** 全部可见，签名与 `docs/design/interop.md` §3 一致

#### Scenario: ABI 版本字段位于首位
- **WHEN** 检查 `Z42TypeDescriptor_v1` 内存布局
- **THEN** `abi_version` 字段 offset = 0；`size_of(uint32_t)` = 4 byte

---

### Requirement: Rust Workspace + Crate Skeleton

`src/runtime/Cargo.toml` 转为 workspace + root package，新增 3 个子 crate（`z42-abi`、`z42-rs`、`z42-macros`），全部编译通过。

#### Scenario: 现有 build 路径不破坏
- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml`
- **THEN** `z42_vm` 二进制和 lib 编译成功（与变更前一致）

#### Scenario: workspace 全员编译
- **WHEN** 执行 `cargo build --workspace --manifest-path src/runtime/Cargo.toml`
- **THEN** 4 个 crate（`z42_vm`、`z42-abi`、`z42-rs`、`z42-macros`）全部编译通过

#### Scenario: ABI 镜像类型布局一致
- **WHEN** `crates/z42-abi/tests/abi_layout_tests.rs` 运行 `mem::offset_of!(Z42TypeDescriptor_v1, abi_version)` 等断言
- **THEN** `abi_version` offset = 0；`flags` offset = 4；`module_name` offset = 8 (64-bit) / 8 (32-bit pointer 系统暂不支持)

#### Scenario: 用户面向 trait 可导入
- **WHEN** 用户 crate 写 `use z42_rs::{Z42Type, Z42Traceable, Z42Args, Z42Value, Z42TypeRef, Z42Error};`
- **THEN** 编译通过；trait/type 名字与 `docs/design/interop.md` 命名一致

---

### Requirement: proc macro Stubs

`z42-macros` crate 暴露 3 个 proc macro 入口（`Z42Type` derive、`methods` attr、`module!` 函数式 macro），实现体在用户使用时报清晰编译错误。

#### Scenario: macro 入口签名注册
- **WHEN** `cargo expand` 或 IDE 检查 `z42-macros` 的 public surface
- **THEN** 能找到 `Z42Type` derive、`methods` attribute、`module!` 三个 proc macro 项

#### Scenario: 用户使用未实现 macro 报清晰错误
- **WHEN** 用户写 `#[derive(Z42Type)]` 并编译
- **THEN** 输出包含 `"#[derive(Z42Type)] is declared in z42-macros but not yet implemented (spec C3)"` 的 `compile_error!`

#### Scenario: macro crate 自身可编译
- **WHEN** `cargo build -p z42-macros`
- **THEN** crate 编译通过（不会因 stub 而 fail）

---

### Requirement: IR Opcode Declarations

C# IR 端和 Rust VM 端各加 4 个新 opcode：`CallNative`、`CallNativeVtable`、`PinPtr`、`UnpinPtr`。

#### Scenario: 字节值固定
- **WHEN** 检查 `Opcodes.cs`
- **THEN** `CallNative = 0x53`，`CallNativeVtable = 0x54`，`PinPtr = 0x90`，`UnpinPtr = 0x91`

#### Scenario: Rust Instruction enum 同步
- **WHEN** 检查 `src/runtime/src/metadata/bytecode.rs::Instruction`
- **THEN** 含对应 4 个变体；字段形状与 C# IR 节点对齐（具体 payload 在 design.md 决议）

#### Scenario: zbc 序列化往返
- **WHEN** 构造含 4 个新 opcode 的 IR `Function`，经 `ZbcWriter` 写出再用 `ZbcReader` 读回
- **THEN** 读回的 `Instruction` 与原始相等（按 record equality）

#### Scenario: VM 遇到新 opcode 报 Trap
- **WHEN** VM 执行含 `CallNative` / `CallNativeVtable` / `PinPtr` / `UnpinPtr` 的字节码
- **THEN** 返回 `VmError::trap` 含 "not implemented (see spec C2/C4)"，**不崩溃，不静默成功**

---

### Requirement: Manifest JSON Schema v1

`docs/design/manifest-schema.json` 是 `.z42abi` v1 的权威 JSON Schema，可用任何 JSON Schema validator 校验。

#### Scenario: schema 自身合法
- **WHEN** 用 JSON Schema meta-schema (Draft 2020-12) 校验 `manifest-schema.json`
- **THEN** 校验通过

#### Scenario: 合法 manifest 通过校验
- **WHEN** `tests/data/example-manifest.json` （仿照 `docs/design/interop.md` §9 例子）按 schema 校验
- **THEN** 校验通过

#### Scenario: 关键字段缺失被拒
- **WHEN** 用缺少 `abi_version` / `module` / `types` 之一的 manifest 校验
- **THEN** 校验失败，错误信息指向缺失字段

#### Scenario: 未知字段被允许
- **WHEN** manifest 含 schema 未声明的额外字段（如 `extra_metadata`）
- **THEN** 校验通过（前向兼容性要求）

---

### Requirement: Error Code Registration

错误码 Z0905–Z0910 在 `docs/design/error-codes.md` 注册占位条目。

#### Scenario: 6 个错误码全部入册
- **WHEN** 检查 `error-codes.md`
- **THEN** 含 Z0905–Z0910 共 6 行，每行标注"Reserved by spec design-interop-interfaces; semantics defined by C2–C5"

#### Scenario: 未与现有错误码冲突
- **WHEN** 解析 `error-codes.md` 全部条目
- **THEN** Z0905–Z0910 与现有 Z0xxx 编号无重复

---

### Requirement: Documentation Sync

接口骨架落地后，相关文档同步更新。

#### Scenario: interop.md Roadmap 更新
- **WHEN** 检查 `docs/design/interop.md` §10 Roadmap
- **THEN** 表中含 C1 行（"L2.M8 prep: Tier 1/2/3 接口骨架"）；§9 引用 `manifest-schema.json` 链接

#### Scenario: ir.md 包含 4 新 opcode 描述
- **WHEN** 检查 `docs/design/ir.md`
- **THEN** 含 `CallNative` / `CallNativeVtable` / `PinPtr` / `UnpinPtr` 4 个 opcode 段落（描述操作数 + 语义占位）

#### Scenario: README 链
- **WHEN** 检查 `src/runtime/README.md` 与 `src/runtime/crates/README.md`
- **THEN** runtime README 提及 `crates/` 子目录及其 3 个 crate；crates README 列出每个子 crate 的职责

## IR Mapping

新增 opcode：

| Opcode | 字节 | 操作数（草案；C2/C4 最终敲定） | 语义占位 |
|--------|-----|-----------------------------|---------|
| `CallNative` | 0x53 | `target: TypeRef + symbol`、`args: [Reg]`、`dst: Reg` | 调用注册的 native 函数（直接符号） |
| `CallNativeVtable` | 0x54 | `recv: Reg`、`vtable_slot: u16`、`args: [Reg]`、`dst: Reg` | 经 native 类型 vtable 间接调用方法 |
| `PinPtr` | 0x90 | `src: Reg (String/Array)`、`dst: Reg (PinnedView)` | 借出 raw ptr+len 视图 |
| `UnpinPtr` | 0x91 | `pinned: Reg` | 释放 pin |

**本 spec 仅声明 + 桩**；真实语义在 C2（Call*）与 C4（Pin*）spec 中钉死。

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [ ] Lexer — 不涉及（无新 token）
- [ ] Parser / AST — 不涉及（无新语法）
- [ ] TypeChecker — 不涉及
- [x] IR Codegen — 加 4 个新 opcode 常量；ZbcReader/Writer 加分支
- [x] VM interp — 加 dispatch 桩（Trap）
- [ ] JIT / AOT — 不涉及（C2/C4 才动）
