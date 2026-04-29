# Tasks: Tier 2 Rust proc macros (C3)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 进度概览

- [x] 阶段 1: z42-abi `unsafe impl Sync` + 工具函数
- [x] 阶段 2: z42-rs 加 macros 重导出 + native_helpers
- [x] 阶段 3: z42-vm 端 `z42_set_panic_message` extern
- [x] 阶段 4: z42-macros 子模块骨架（lib.rs 路由 + util.rs）
- [x] 阶段 5: 签名映射 (signature.rs)
- [x] 阶段 6: shim 代码生成 (shim.rs)
- [x] 阶段 7: `#[z42::methods]` 主实现 (methods_attr.rs)
- [x] 阶段 8: `module!` 实现 (module_macro.rs)
- [x] 阶段 9: `#[derive(Z42Type)]` / `#[z42::trait_impl]` 更新错误信息
- [x] 阶段 10: trybuild 单元测试
- [x] 阶段 11: numz42-rs PoC + e2e 集成测试
- [x] 阶段 12: 文档同步
- [x] 阶段 13: 全绿验证 + 归档

---

## 阶段 1: z42-abi `unsafe impl Sync`

- [x] 1.1 修改 `crates/z42-abi/src/lib.rs`：加 `unsafe impl Sync for Z42TypeDescriptor_v1 / Z42MethodDesc / Z42FieldDesc / Z42MethodImpl / Z42TraitImpl {}`
- [x] 1.2 在 `crates/z42-abi/tests/abi_layout_tests.rs` 加一项 `#[test] fn descriptor_is_sync()` （`fn assert_sync<T: Sync>() {} assert_sync::<Z42TypeDescriptor_v1>();`）
- [x] 1.3 `cargo test -p z42-abi` 通过

## 阶段 2: z42-rs 重导出 macros + native_helpers

- [x] 2.1 修改 `crates/z42-rs/Cargo.toml`：加 `z42-macros = { path = "../z42-macros" }`
- [x] 2.2 修改 `crates/z42-rs/src/lib.rs`：`pub use z42_macros::{methods, module, trait_impl};`；prelude 加 macros 入口
- [x] 2.3 创建 `crates/z42-rs/src/native_helpers.rs`：`set_panic(&str)` + extern fn forward
- [x] 2.4 `cargo build -p z42-rs` 通过

## 阶段 3: z42-vm 端 `z42_set_panic_message`

- [x] 3.1 修改 `src/runtime/src/native/exports.rs`：加 `#[no_mangle] pub extern "C" fn z42_set_panic_message(msg: *const c_char)`，调 `error::set(Z0905, ...)`
- [x] 3.2 添加单元测试：调 `z42_set_panic_message`，验证 `error::last()` 返回 Z0905

## 阶段 4: z42-macros 子模块骨架

- [x] 4.1 修改 `crates/z42-macros/Cargo.toml`：加 `convert_case = "0.6"`（PascalCase ↔ snake_case ident 转换）
- [x] 4.2 重写 `crates/z42-macros/src/lib.rs`：仅放 `#[proc_macro*]` 入口，路由到子模块
- [x] 4.3 创建 `crates/z42-macros/src/util.rs`：c-string literal 生成、ident 转换、`syn::Error::to_compile_error()` 包装
- [x] 4.4 占位 `methods_attr.rs` / `module_macro.rs` / `signature.rs` / `shim.rs`，仍报 `compile_error!`

## 阶段 5: 签名映射 (signature.rs)

- [x] 5.1 创建 `crates/z42-macros/src/signature.rs`：
  - `pub enum AbiTy { I8/I16/I32/I64/U8/.../F32/F64/Bool/Void/Ptr{mutable: bool}/SelfRef{mutable: bool}/CStr }`
  - `pub fn parse_rust_type(ty: &syn::Type) -> Result<AbiTy, syn::Error>` —— 处理 path types (`i32` 等), `&self` / `&mut self` 通过 receiver 单独处理
  - `pub fn render_signature_string(receiver: Option<AbiTy>, params: &[AbiTy], ret: &AbiTy) -> String`
- [x] 5.2 单元测试 `signature_tests.rs`（同目录）：每条映射 + 拒绝 String / Vec 等
- [x] 5.3 完整覆盖 design §Decision 4 表

## 阶段 6: shim 代码生成 (shim.rs)

- [x] 6.1 创建 `crates/z42-macros/src/shim.rs`：
  - `pub fn render_shim(ty_ident, method_ident, receiver, params, ret) -> TokenStream`
  - 输出形式见 design §Decision 5
  - 处理 `&self` / `&mut self` / 静态方法 / 不同返回类型 / panic 兜底
- [x] 6.2 单元测试：手工构造 syn::ImplItemFn，验证 token 流符合预期

## 阶段 7: `#[z42::methods]` 主实现

- [x] 7.1 解析 attribute 参数 `module = "..."` / `name = "..."`（用 `syn::parse::Parse` + comma-separated key=value）
- [x] 7.2 解析 `impl T { ... }`（用 `syn::parse_macro_input!`）：拒绝 generic / lifetime / async / unsafe impl
- [x] 7.3 遍历 impl items：
  - `ImplItem::Fn` → 生成 shim + 收集 method_desc 字面量
  - 其他（const / type 等）→ 透传
- [x] 7.4 emit 全部 token：
  - 原 `impl T { ... }`（不动）
  - shim 函数 N 个
  - `static __<T>_METHODS: [Z42MethodDesc; N] = [...]`
  - `static __<T>_DESC: Z42TypeDescriptor_v1 = ...`
  - `impl Z42Type for T { ... }`
- [x] 7.5 trybuild 测试：pass case 编译、fail case 含期望诊断

## 阶段 8: `module!` 实现

- [x] 8.1 解析 `name: "...", types: [T1, T2, ...]` 语法
- [x] 8.2 校验 `name` 字符集（`[A-Za-z_][A-Za-z0-9_]*`）
- [x] 8.3 生成 `#[no_mangle] pub extern "C" fn <name>_register() { ... }`
- [x] 8.4 trybuild 测试

## 阶段 9: derive / trait_impl 错误信息更新

- [x] 9.1 `derive_z42_type` 改 message："C3 主入口是 #[z42::methods]；derive 推迟到 source generator (C5)"
- [x] 9.2 `trait_impl` message：与 trait 形状一并在 source generator (C5) 实现

## 阶段 10: trybuild 单元测试

- [x] 10.1 在 `crates/z42-macros/Cargo.toml` 加 `[dev-dependencies] trybuild = "1"`
- [x] 10.2 创建 `crates/z42-macros/tests/expand_smoke.rs` + `tests/pass/*.rs` + `tests/fail/*.rs`
- [x] 10.3 覆盖 spec scenarios 的每条 fail case

## 阶段 11: numz42-rs PoC + e2e

- [x] 11.1 创建 `src/runtime/tests/data/numz42-rs/mod.rs`：Counter Rust 版 + `#[z42::methods]` + `module!`
- [x] 11.2 修改 `src/runtime/tests/native_interop_e2e.rs`：
  - 引入 `mod numz42_rs;`（path = `data/numz42-rs/mod.rs`）
  - 加 `rust_counter_register_and_invoke()` 测试
  - 加 `c_and_rust_modules_coexist()` 测试
- [x] 11.3 验证 Rust Counter alloc → inc×3 → get → I64(3)

## 阶段 12: 文档同步

- [x] 12.1 修改 `docs/design/interop.md` §10 Roadmap C3 行 → ✅ + 日期
- [x] 12.2 修改 `docs/roadmap.md` Native Interop 表 C3 → ✅
- [x] 12.3 重写 `crates/z42-macros/README.md`：状态 + 用法示例 + 子模块说明
- [x] 12.4 更新 `crates/z42-rs/README.md`：prelude 用法示例

## 阶段 13: 全绿验证 + 归档

- [x] 13.1 `cargo build --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 13.2 `cargo test --workspace --manifest-path src/runtime/Cargo.toml`（含 trybuild）
- [x] 13.3 `dotnet build src/compiler/z42.slnx` + `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [x] 13.4 `./scripts/test-vm.sh`
- [x] 13.5 输出验证报告
- [x] 13.6 spec scenarios 1:1 对照
- [x] 13.7 移动 spec/changes/impl-tier2-rust-macros/ → spec/archive/2026-04-29-impl-tier2-rust-macros/
- [x] 13.8 commit + push（不带 .claude/settings*.json）

## 备注

- C3 范围明显小于 C2（不动 z42-vm 内部，主要在 z42-macros 这个 proc-macro crate）
- numz42-c PoC 保留作对照；新 Rust PoC 从行为上必须等价（同样 alloc → inc×3 → get → 3）
- trybuild 测试可能在某些 CI 环境因 toolchain mismatch 误报；用 `TRYBUILD=overwrite` 重新生成 stderr 基线（手工验证后）
