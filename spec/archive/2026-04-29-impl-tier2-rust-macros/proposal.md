# Proposal: Implement Tier 2 Rust proc macros (C3)

## Why

C1 已经把 `Z42Type` derive、`#[z42::methods]`、`#[z42::trait_impl]`、`z42::module!` 四个 proc macro 注册了入口签名（IDE 能看到、`cargo check` 通过），但展开时全部报 `compile_error!` 指向 C3。C2 已让 Tier 1 C ABI 完整通电（Counter PoC 用纯 C 写）。

C3 把 Tier 2 这一层接通：让 Rust 用户不必手写 `Z42TypeDescriptor_v1` 字面量，只需 `#[derive(Z42Type)]` + `#[z42::methods]` + `module!` 就能把任意 Rust 类型暴露给 z42。这是 z42 native 生态的主入口（`z42-std-*` 全部依赖此层）。

## What Changes

- **`z42-macros` crate 实现 2 个核心 proc macro** 的真实展开（详见 design §Decision 1 为何砍到 2 个）：
  - `#[z42::methods(module = "...", name = "...")]` 作用于 `impl T { ... }` —— **C3 主入口**。一次性生成 descriptor、方法表、所有 `extern "C"` shim、`Z42Type` impl
  - `z42::module! { name: "...", types: [T1, T2, ...] }` —— 生成 `#[no_mangle] pub extern "C" fn <module>_register()`，依次调 `z42_register_type` 把每个类型推入 VM
- **`#[derive(Z42Type)]` 与 `#[z42::trait_impl]` 在 C3 暂不实现**，仍保留 `compile_error!` stub。derive 在 source generator (C5) 阶段与 trait 形状一并设计；trait_impl 同步推迟（Rust proc macro 之间累积静态数组的方案需要等 C5 决定）
- **`z42-rs` 重导出 macros**：`pub use z42_macros::{Z42Type, methods, trait_impl, module};`，让用户写 `use z42_rs::prelude::*; #[derive(Z42Type)]`
- **`numz42-rs` PoC**：在 z42_vm 测试目录新增 Rust 版 Counter（与 C 版同形状），通过宏注册，端到端跑通 alloc → inc×3 → get → I64(3)
- **`tests/native_interop_e2e.rs` 扩展**：加 `rust_counter_register_and_invoke` 等场景，验证宏生成的 descriptor 与手写 C descriptor 行为一致
- **错误码 Z0907 不在本 spec 启用**（仍属 C5）；本 spec 不引入新错误码
- **文档同步**：`docs/design/interop.md` §10 C3 行 → ✅；`crates/z42-macros/README.md` 更新；`crates/z42-rs/README.md` 加 prelude 说明
- **兼容性**：C 写的 `numz42-c` PoC 保留，与 Rust 版并存验证 ABI 中立

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/crates/z42-macros/Cargo.toml` | MODIFY | 加 `convert_case = "0.6"` 用于 PascalCase ↔ snake_case |
| `src/runtime/crates/z42-macros/src/lib.rs` | MODIFY | 4 个 macro 实现入口（路由到子模块）|
| `src/runtime/crates/z42-macros/src/derive_z42_type.rs` | NEW | `#[derive(Z42Type)]` 展开逻辑 |
| `src/runtime/crates/z42-macros/src/methods_attr.rs` | NEW | `#[z42::methods]` 展开逻辑 |
| `src/runtime/crates/z42-macros/src/trait_impl_attr.rs` | NEW | `#[z42::trait_impl]` 展开逻辑 |
| `src/runtime/crates/z42-macros/src/module_macro.rs` | NEW | `module!` 展开逻辑 |
| `src/runtime/crates/z42-macros/src/util.rs` | NEW | 共享工具：c-string literal 生成、ident 转换、错误辅助 |
| `src/runtime/crates/z42-macros/tests/expand_smoke.rs` | NEW | 用 `trybuild` 验证常见正确 / 错误用例展开 |
| `src/runtime/crates/z42-rs/Cargo.toml` | MODIFY | 加 `z42-macros = { path = "../z42-macros" }` 依赖 |
| `src/runtime/crates/z42-rs/src/lib.rs` | MODIFY | `pub use z42_macros::{Z42Type, methods, trait_impl, module};` 入 prelude |
| `src/runtime/crates/z42-rs/src/traits.rs` | MODIFY | `Z42Type` trait 加可选 helpers（`fn descriptor_static() -> &'static Descriptor;` 等）以便宏展开引用 |
| `src/runtime/tests/data/numz42-rs/mod.rs` | NEW | 测试内联 PoC：`Counter` Rust 版（`#[derive(Z42Type)]` + `#[methods]` + `module!`）|
| `src/runtime/tests/native_interop_e2e.rs` | MODIFY | 增加 `rust_counter_*` 场景；用辅助函数共享 IR 构造 |
| `src/runtime/crates/z42-macros/README.md` | MODIFY | 状态从"C1 接口骨架"改为"C3 实现完成 + 用法示例"|
| `src/runtime/crates/z42-rs/README.md` | MODIFY | prelude 增加 macro 说明 |
| `docs/design/interop.md` | MODIFY | §10 Roadmap C3 行 → ✅ + 完成日期 |
| `docs/roadmap.md` | MODIFY | Native Interop 表 C3 → ✅ |

**只读引用**：
- `src/runtime/crates/z42-abi/src/lib.rs` — 镜像类型 + 常量
- `src/runtime/include/z42_abi.h` — C 头规范
- `docs/design/interop.md` §4（Tier 2 设计）
- `src/runtime/tests/data/numz42-c/numz42.c` — C PoC 作为对照
- `src/runtime/src/native/registry.rs` — descriptor 字段消费方

## Out of Scope

- **`pinned` 块**：String/Array 借出仍 trap（C4）
- **`CallNativeVtable`** 仍 trap（C5）
- **manifest 生成**：宏在 C3 不写 `.z42abi` 文件（C5 做这事）
- **`#[ctor]` / 自动注册**：本 spec 用显式 `<module>_register` 函数；自动注册留给 C5（与 source generator 联动决定）
- **泛型 / lifetime**：`#[derive(Z42Type)]` 仅支持无泛型的 `struct T { ... }`；带 `<T>` 的拒绝并报清晰错误
- **接收异步函数 / generic methods**：`#[z42::methods]` 拒绝
- **JIT 后端 hot-path**：现状是 interp dispatch；JIT 接入是 L3.M16

## Open Questions

- [ ] **Q1**：`Z42Type` trait 形状要不要改？C1 是 `fn descriptor() -> *const Descriptor`；`#[derive]` 展开生成 static 后，其实 `&'static Descriptor` 比 `*const` 更类型安全
  - 倾向：trait 加 `fn descriptor_static() -> &'static Descriptor` 默认实现走 `*const` 转换；保留旧方法兼容；后续可弃旧版本
- [ ] **Q2**：retain/release 默认实现策略
  - 倾向：descriptor 默认 `None`（Tier 1 已支持 None → 只有单 owner，多次释放会报错）；用户加 `#[z42(rc_field = "_z42_rc")]` 字段属性后，宏生成 `AtomicU32` 风格的 retain/release
  - C3 PoC（Counter Rust 版）跟 C 版一致，自带 `_z42_rc: AtomicU32`
- [ ] **Q3**：方法签名解析在宏展开时还是运行时？
  - 倾向：宏展开时把每个方法签名渲染成签名字符串（`"(*mut Self) -> i64"`），放进 descriptor `methods[i].signature` 字段；运行时仍用现有 `dispatch::parse_signature` 拿 cif。这避免 macros 直接操作 libffi
