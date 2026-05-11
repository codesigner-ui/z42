# Tasks: Add ZpkgResolver Hook

> 状态：🔵 DRAFT（未实施） | 创建：2026-05-11
> 前置：[`spec/archive/2026-05-10-add-embedding-api/`](../../archive/2026-05-10-add-embedding-api/) 完成（已 ✅）
> 下游：[`add-platform-ios`](../add-platform-ios/) / [`add-platform-android`](../add-platform-android/) / [`add-platform-wasm`](../add-platform-wasm/) —— 三平台 spec 的实施必须在本 spec 之后

## 进度概览

- [ ] 阶段 1: Tier 1 C ABI append
- [ ] 阶段 2: Tier 1 Rust 内部数据结构 + resolver.rs
- [ ] 阶段 3: build_host_module 改造
- [ ] 阶段 4: Tier 2 Rust trait + 内置 resolver
- [ ] 阶段 5: 单元 + 集成测试
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 验证 + commit

---

## 阶段 1: Tier 1 C ABI append

- [ ] 1.1 [`src/runtime/include/z42_host.h`](../../../../src/runtime/include/z42_host.h) 在 `Z42WriteSink` typedef 后加 `Z42ZpkgResolverFn` typedef + 文档
- [ ] 1.2 同文件 `Z42HostConfig` 结构在 `search_paths` 之后 append 两个字段
- [ ] 1.3 [`src/runtime/include/README.md`](../../../../src/runtime/include/README.md) "类型" 行加 `Z42ZpkgResolverFn`

## 阶段 2: Tier 1 Rust state + resolver.rs

- [ ] 2.1 [`src/runtime/src/host/config.rs`](../../../../src/runtime/src/host/config.rs) `Z42HostConfig` C 镜像同步 append 两个字段
- [ ] 2.2 同文件 `ResolvedConfig` 加 `zpkg_resolver: Option<Arc<dyn ZpkgResolver>>` 字段；`validate` 把 C 字段（fn ptr + user_data）包成 `CHookResolver` → `Arc<dyn>`
- [ ] 2.3 [`src/runtime/src/host/resolver.rs`](../../../../src/runtime/src/host/resolver.rs) NEW
  - `pub trait ZpkgResolver: Send + Sync { fn resolve(&self, namespace: &str) -> Option<Vec<u8>>; }`
  - `pub(crate) struct CHookResolver { callback: Z42ZpkgResolverFn, user_data: usize }`
  - `unsafe impl Send + Sync for CHookResolver`
  - `impl ZpkgResolver for CHookResolver` 调 callback、复制 bytes、返回 `Some(Vec)` / `None`
- [ ] 2.4 [`src/runtime/src/host/mod.rs`](../../../../src/runtime/src/host/mod.rs) `pub mod resolver;`
- [ ] 2.5 [`src/runtime/src/host/state.rs`](../../../../src/runtime/src/host/state.rs) `HostState` 加 `pub resolver: Option<Arc<dyn ZpkgResolver>>`；`try_initialize` 接受额外参数

## 阶段 3: build_host_module 改造

- [ ] 3.1 [`src/runtime/src/host/ops.rs`](../../../../src/runtime/src/host/ops.rs) `build_host_module` 签名加 `resolver: Option<&Arc<dyn ZpkgResolver>>` 参数
- [ ] 3.2 实现解析顺序：
  - 总是先请求 `resolver.resolve("z42.core")`（如有 resolver）
  - 对 `user_artifact.import_namespaces` 中每个 ns 先调 resolver
  - resolver miss 后回退现有 `search_paths` + `resolve_namespace` 路径
  - 所有命中的 zpkg bytes 走 `load_artifact_from_bytes` 统一 merge
- [ ] 3.3 [`src/runtime/src/host/mod.rs`](../../../../src/runtime/src/host/mod.rs) `z42_host_load_zbc` 把 `state.resolver` 透传给 `build_host_module`

## 阶段 4: Tier 2 Rust trait + 内置 resolver

- [ ] 4.1 [`src/toolchain/host/embed/src/lib.rs`](../../../../src/toolchain/host/embed/src/lib.rs) `pub use z42_vm::host::resolver::ZpkgResolver`
- [ ] 4.2 同文件 `HostConfig` 加 `pub zpkg_resolver: Option<Arc<dyn ZpkgResolver>>`
- [ ] 4.3 同文件 `pub struct MapResolver` + impl ZpkgResolver
- [ ] 4.4 同文件 `pub struct SearchPathsResolver` + impl ZpkgResolver
- [ ] 4.5 `Host::new` 把 `cfg.zpkg_resolver` 透传给 Tier 1 C `Z42HostConfig`
  - 若用 Rust trait object：直接通过新内部 setter 写入 Tier 1 state（绕过 C 包装）
  - 若用 C fn ptr：走 `cfg.zpkg_resolver` C 字段

## 阶段 5: 单元 + 集成测试

- [ ] 5.1 [`src/runtime/src/host/host_tests.rs`](../../../../src/runtime/src/host/host_tests.rs) 加 5 个测试：
  - `resolver_via_map_resolver_loads_corelib_without_search_paths`
  - `resolver_via_c_hook_loads_corelib`
  - `resolver_miss_falls_back_to_search_paths`
  - `resolver_both_unset_load_zbc_succeeds_if_zbc_self_contained`
  - `resolver_corelib_miss_then_console_writeline_fails_at_invoke`
- [ ] 5.2 复用现有 `tests/data/embedding_hello/source.z42` fixture；不新增 .z42

## 阶段 6: 文档同步

- [ ] 6.1 [`docs/design/runtime/embedding.md`](../../../design/runtime/embedding.md)
  - §4.2 `Z42HostConfig` 字段表加 `zpkg_resolver` / `zpkg_resolver_user_data`
  - 新增 §11 ZpkgResolver 节（trait / 内置 / C hook / 优先级 / 生命周期）
- [ ] 6.2 [`src/toolchain/host/README.md`](../../../../src/toolchain/host/README.md) Tier 2 API 段加 `ZpkgResolver` 说明
- [ ] 6.3 [`src/toolchain/host/platforms/README.md`](../../../../src/toolchain/host/platforms/README.md) "ZpkgResolver 协议" 段链接定稿
- [ ] 6.4 [`docs/roadmap.md`](../../../roadmap.md) L2 Embedding 段加 H4-prereq 行

## 阶段 7: 验证

- [ ] 7.1 `cargo build` × 4 feature preset 全绿（default / interp-only / ios / android）
- [ ] 7.2 `cargo test --manifest-path src/runtime/Cargo.toml --lib host::` 至少 17 + 5 = 22 测试通过
- [ ] 7.3 `cargo test --manifest-path src/runtime/Cargo.toml` 整体 0 fail
- [ ] 7.4 hello_rust example 仍能跑（向后兼容）
- [ ] 7.5 commit + push + archive 到 `spec/archive/YYYY-MM-DD-add-zpkg-resolver-hook/`

---

## 备注

### 工作量估计

1–1.5 天（绝大部分是 trait 定义 + adapter；runtime build_host_module 改造已熟悉）。

### 实施依赖

- ✅ `spec/archive/2026-05-10-add-embedding-api/` 全部已完成
- ✅ `load_artifact_from_bytes` 已存在
- ✅ `Z42HostConfig` 已是 `#[repr(C)]` 且尾部可扩展

### 与并行 spec 的协调

本 spec 完成前**不可启动** `add-platform-ios` / `add-platform-android` / `add-platform-wasm` 的代码实施。三个 platform spec 的 design 已根据本 spec 的接口定稿（见同期修订）。
