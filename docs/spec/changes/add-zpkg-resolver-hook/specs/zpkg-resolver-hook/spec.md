# Spec: ZpkgResolver Hook

## ADDED Requirements

### Requirement: Tier 1 C ABI surface

#### Scenario: z42_host.h defines Z42ZpkgResolverFn

- **WHEN** 阅读 [`src/runtime/include/z42_host.h`](../../../../../../src/runtime/include/z42_host.h)
- **THEN** 含 `typedef int (*Z42ZpkgResolverFn)(const char*, const uint8_t**, size_t*, void*);`
- **AND** typedef 注释说明 hit 返回非 0、miss 返回 0；bytes 仅需在 callback 内有效

#### Scenario: Z42HostConfig appended fields (ABI version unchanged)

- **WHEN** 阅读 `Z42HostConfig` struct 定义
- **THEN** 在末尾（`search_paths` 之后）含字段 `Z42ZpkgResolverFn zpkg_resolver;` + `void* zpkg_resolver_user_data;`
- **AND** `#define Z42_HOST_ABI_VERSION` 仍为 `1`（append-only 不 bump）

---

### Requirement: Tier 2 Rust trait + 内置实现

#### Scenario: ZpkgResolver trait

- **WHEN** 阅读 [`src/toolchain/host/embed/src/lib.rs`](../../../../../../src/toolchain/host/embed/src/lib.rs)
- **THEN** 含 `pub trait ZpkgResolver: Send + Sync` 且唯一方法签名为 `fn resolve(&self, namespace: &str) -> Option<Vec<u8>>`

#### Scenario: HostConfig 接受 resolver

- **WHEN** 阅读 `pub struct HostConfig`
- **THEN** 含字段 `pub zpkg_resolver: Option<Arc<dyn ZpkgResolver>>`

#### Scenario: 内置 MapResolver

- **WHEN** 阅读 `MapResolver` 实现
- **THEN** 含 `pub fn new() -> Self` / `pub fn insert(&mut self, namespace: &str, bytes: Vec<u8>)`
- **AND** `impl ZpkgResolver for MapResolver` 把 `resolve` 实现为 `HashMap::get(namespace).cloned()`

#### Scenario: 内置 SearchPathsResolver

- **WHEN** 阅读 `SearchPathsResolver` 实现
- **THEN** 行为等价于 v0.1 默认 `search_paths` 路径扫描

---

### Requirement: Runtime 解析顺序

#### Scenario: resolver 优先，search_paths fallback

- **GIVEN** `Z42HostConfig` 同时设了 `zpkg_resolver` 与 `search_paths`
- **AND** user `.zbc` 含 `using Std.IO`
- **WHEN** 调用 `z42_host_load_zbc`
- **THEN** runtime 先调 `resolver(namespace="Std.IO")`；hit 则用其字节
- **AND** miss 后才扫 `search_paths` 找对应 zpkg
- **AND** 两条路径解析出的 module 进入同一 `merge_modules` 流程

#### Scenario: corelib 总是被请求

- **GIVEN** user `.zbc` 没有任何 `using` 语句
- **WHEN** 调用 `z42_host_load_zbc`
- **THEN** runtime 仍调 `resolver.resolve("z42.core")`
- **AND** 若 miss + search_paths 也无 corelib → load 成功；但任何引用 corelib 类型的 invoke 报 `VM_EXCEPTION`（"undefined function ..."）

#### Scenario: resolver miss 不算 load 失败

- **GIVEN** resolver 对所有请求返回 0；search_paths 为空
- **WHEN** 调用 `z42_host_load_zbc` 加载一个**自包含**的 `.zbc`（无 import）
- **THEN** 返回 `Z42_HOST_OK`

---

### Requirement: C hook bytes 生命周期

#### Scenario: callback 返回后字节可释放

- **GIVEN** host 实现的 `Z42ZpkgResolverFn` 在 callback 内分配栈缓冲，返回前赋给 `*out_bytes`
- **WHEN** runtime 用该 callback 解析 zpkg
- **THEN** runtime 在 callback 返回**前**完成 bytes 读取 / 复制
- **AND** callback 返回后 host 立即释放缓冲不会引起 use-after-free

#### Scenario: callback 返回 0 = miss

- **WHEN** Host 的 `Z42ZpkgResolverFn` 对某 namespace 返回 0
- **THEN** runtime 不读 `*out_bytes` / `*out_length`，视为 miss 继续 fallback

---

### Requirement: Tier 2 与 Tier 1 互通

#### Scenario: Rust trait 实例可经 Tier 2 → Tier 1 透传

- **GIVEN** 用 `MapResolver` 构造 `HostConfig::zpkg_resolver`
- **WHEN** `Host::new(cfg)` 调用 `z42_host_initialize`
- **THEN** runtime 把 Rust trait object 直接存入 `HostState`（不经过 C 回调适配）

#### Scenario: C 回调可在 Tier 1 上自适配为 Rust trait

- **GIVEN** C 客户端直接调 `z42_host_initialize` 并设 `cfg.zpkg_resolver`
- **WHEN** runtime 在 `validate` 期间构造 `HostState`
- **THEN** runtime 用 `CHookResolver`（私有结构体）把 C fn 包装成 `Arc<dyn ZpkgResolver>` 存入 state
- **AND** 后续 `load_zbc` 经由此 adapter 透明触发 C 回调

---

### Requirement: 文档同步

#### Scenario: embedding.md §11 ZpkgResolver 节

- **WHEN** 阅读 [`docs/design/runtime/embedding.md`](../../../../../design/runtime/embedding.md)
- **THEN** 含 §11 ZpkgResolver 节，说明 trait / 内置 / C hook / 优先级 / 生命周期

#### Scenario: §4.2 Z42HostConfig 字段表更新

- **WHEN** 阅读 §4.2 Z42HostConfig
- **THEN** `search_paths` 之后追加 `zpkg_resolver` + `zpkg_resolver_user_data` 行，说明用途

---

### Requirement: 测试覆盖

#### Scenario: host_tests 含 5 个 resolver 测试

- **WHEN** 阅读 [`src/runtime/src/host/host_tests.rs`](../../../../../../src/runtime/src/host/host_tests.rs)
- **THEN** 含测试名（顺序不限）：
  - `resolver_via_map_resolver_loads_corelib_without_search_paths`
  - `resolver_via_c_hook_loads_corelib`
  - `resolver_miss_falls_back_to_search_paths`
  - `resolver_both_unset_load_zbc_succeeds_if_zbc_self_contained`
  - `resolver_corelib_miss_then_console_writeline_fails_at_invoke`

#### Scenario: 所有测试通过

- **WHEN** 执行 `cargo test --manifest-path src/runtime/Cargo.toml --lib host::`
- **THEN** 含 H1 + H2 + H3 既有 17 测试 + 本 spec 5 新测试，全部通过

---

### Requirement: 向后兼容

#### Scenario: 现有 search_paths-only 调用不受影响

- **GIVEN** 客户端代码沿用 `HostConfig::default()` + 只设 `search_paths`
- **WHEN** `Host::new(cfg)` + `load_zbc`
- **THEN** 行为与本 spec 落地前完全一致（resolver 字段为 `None` → runtime 跳过 resolver 路径）

#### Scenario: ABI version 不动

- **WHEN** 编译时打印 `Z42_HOST_ABI_VERSION`
- **THEN** 仍为 `1`
- **AND** 用旧 ABI v1 头编出来的客户端能与新 runtime 链接（cfg 末尾字段被 runtime 当 NULL 处理）
