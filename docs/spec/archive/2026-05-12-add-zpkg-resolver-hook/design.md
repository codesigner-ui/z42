# Design: ZpkgResolver Hook

> 详细 user-facing API 见 [`docs/design/runtime/embedding.md`](../../../design/runtime/embedding.md) §11（本 spec 实施时同步建好）。
> 本文聚焦**实施级**决策。

## Architecture

```
                    ┌──────────────────────────────────────┐
                    │  Host application                    │
                    │   - 设 zpkg_resolver = Arc<MyImpl>   │
                    └──────────────┬───────────────────────┘
                                   │
                                   ▼
                    ┌──────────────────────────────────────┐
                    │  z42-host (Tier 2)                   │
                    │   HostConfig { zpkg_resolver, ... }  │
                    │   → 注入到 Tier 1 C HostConfig       │
                    │     (wrap closure→sink_trampoline)   │
                    └──────────────┬───────────────────────┘
                                   │ extern "C"
                                   ▼
                    ┌──────────────────────────────────────┐
                    │  z42_vm::host (Tier 1)               │
                    │   resolver: Option<Arc<dyn ZpkgRes>> │
                    └──────────────┬───────────────────────┘
                                   │
            build_host_module ─────┤
            (load_zbc 入口)        │
                                   ▼
        ┌─────────────────────────────────────────────────┐
        │  for ns in {"z42.core"} + user.import_namespaces:│
        │      1. try resolver.resolve(ns)                 │
        │         hit → load_artifact_from_bytes → merge   │
        │      2. miss → fallback search_paths scan        │
        │         (现有 resolve_namespace 路径)            │
        └─────────────────────────────────────────────────┘
```

## D1. C 回调签名

```c
typedef int (*Z42ZpkgResolverFn)(
    const char*       namespace_name,
    const uint8_t**   out_bytes,
    size_t*           out_length,
    void*             user_data);
```

返回值约定：
- **非 0** = 成功；`*out_bytes` / `*out_length` 已写入；bytes **只需在 callback 返回前有效**（runtime 立即 parse 并丢）
- **0** = miss；`*out_bytes` / `*out_length` 值忽略；runtime 继续 fallback 链

为何用 `int` 而非 `Z42HostStatus`：resolver 调用没有"runtime 错误码"语义；只有 hit/miss。其他失败（如 host 内部读取出错）由 host 在 callback 里自己处理 — 想报错就让 callback 返回 0 + 内部 log；想 abort 整个 load_zbc 就在 callback 里 `set_error()` 后返回 0，runtime 的 fallback 拿不到也会 fail。

为何 bytes 生命周期不延长：runtime 在 callback scope 内立即 `read_zbc(bytes)` + `merge_modules`，之后只持有解析后的 `Module`。bytes 不出 callback 是最简明的契约（参考 SQLite 的 `xRead` 风格）。

## D2. Tier 2 trait + 内置实现

```rust
// src/toolchain/host/embed/src/lib.rs
pub trait ZpkgResolver: Send + Sync {
    fn resolve(&self, namespace: &str) -> Option<Vec<u8>>;
}

/// HashMap-based eager resolver. Host pre-populates all known zpkgs;
/// resolve = trivial map lookup. Use case: mobile apps that bundle
/// all stdlib zpkgs as resources and load them at startup.
pub struct MapResolver {
    map: std::collections::HashMap<String, Vec<u8>>,
}
impl MapResolver {
    pub fn new() -> Self { ... }
    pub fn insert(&mut self, namespace: &str, bytes: Vec<u8>) { ... }
}
impl ZpkgResolver for MapResolver { ... }

/// Filesystem-based resolver wrapping the legacy `search_paths` scan.
/// Equivalent to the v0.1 default behaviour; useful for desktop hosts
/// that want explicit resolver chaining.
pub struct SearchPathsResolver {
    paths: Vec<PathBuf>,
}
impl ZpkgResolver for SearchPathsResolver { ... }
```

为什么不在 Tier 2 起就把 resolver 暴露为 trait object 而非泛型：trait object（`Arc<dyn ZpkgResolver>`）方便宿主语言（如 wasm）做 JS-callback 适配 — 一个固定大小指针穿 FFI 更稳。性能上 resolver 调用频率极低（每个 namespace 一次，整次 load_zbc 最多 ~5 次），动态分发开销可忽略。

## D3. Runtime 集成（Tier 1 内部数据流）

```rust
// src/runtime/src/host/state.rs
pub(crate) struct HostState {
    pub config: ResolvedConfig,
    pub resolver: Option<Arc<dyn ZpkgResolver>>,   // NEW
    pub modules: Vec<HostModule>,
    pub entries: Vec<HostEntry>,
    pub corelib: Option<HostCorelib>,              // 仍保留供 search_paths 路径
}

// src/runtime/src/host/resolver.rs (NEW)
pub trait ZpkgResolver: Send + Sync {
    fn resolve(&self, namespace: &str) -> Option<Vec<u8>>;
}

/// Adapter: wrap a C function pointer + user_data as a Rust ZpkgResolver.
pub(crate) struct CHookResolver {
    pub callback: Z42ZpkgResolverFn,
    pub user_data: usize,    // *mut c_void as usize for Send + Sync
}
unsafe impl Send for CHookResolver {}
unsafe impl Sync for CHookResolver {}
impl ZpkgResolver for CHookResolver {
    fn resolve(&self, namespace: &str) -> Option<Vec<u8>> {
        let cname = CString::new(namespace).ok()?;
        let mut bytes_ptr: *const u8 = std::ptr::null();
        let mut length: usize = 0;
        let rc = unsafe {
            (self.callback)(cname.as_ptr(), &mut bytes_ptr, &mut length,
                            self.user_data as *mut c_void)
        };
        if rc == 0 || bytes_ptr.is_null() || length == 0 {
            return None;
        }
        // Copy bytes out of the callback scope so the runtime can hold them.
        let slice = unsafe { std::slice::from_raw_parts(bytes_ptr, length) };
        Some(slice.to_vec())
    }
}
```

`CHookResolver` 在 `validate(cfg)` 期间从 `Z42HostConfig.zpkg_resolver` 字段构造。Tier 2 的 `Arc<dyn ZpkgResolver>` 走另一条路径直接装进 `HostState.resolver`，不经过 C 回调（性能 + 简洁）。

## D4. `build_host_module` 改造

```rust
pub(crate) fn build_host_module(
    bytes: &[u8],
    corelib_legacy: Option<&HostCorelib>,         // 旧 search_paths 路径，向后兼容
    resolver: Option<&Arc<dyn ZpkgResolver>>,     // NEW
) -> Result<HostModule> {
    let user_artifact = load_artifact_from_bytes(bytes)?;
    let user_module_name = user_artifact.module.name.clone();
    let mut modules: Vec<Module> = Vec::with_capacity(4);
    let mut initially_loaded: Vec<String> = Vec::new();

    // (1) corelib — resolver 优先，miss 回退 search_paths
    let corelib_namespaces = ["z42.core"];
    for ns in corelib_namespaces.iter().chain(user_artifact.import_namespaces.iter().map(|s| s.as_str())) {
        if let Some(r) = resolver {
            if let Some(zpkg_bytes) = r.resolve(ns) {
                let dep = load_artifact_from_bytes(&zpkg_bytes)?;
                modules.push(dep.module);
                initially_loaded.push(format!("{ns}.zpkg"));  // 名义文件名
                continue;
            }
        }
        // fallback: search_paths
        if let Some(c) = corelib_legacy {
            // 现有逻辑：扫 libs_dir
            ...
        }
    }
    // ... rest as before
}
```

corelib `"z42.core"` 是**总是请求**的（user `.zbc` 哪怕没写 `using` 也需要 Object 等基础类型）。如果 resolver 与 search_paths 都 miss，silent 继续 —— 用户代码引用 corelib 类型时 runtime 才报"undefined function"。Q4 决议落地。

## D5. ABI append-only 保证

`Z42HostConfig` 当前布局：

```c
typedef struct Z42HostConfig {
    uint32_t  abi_version;
    uint32_t  reserved;
    Z42ExecMode exec_mode;
    size_t heap_initial_bytes;
    size_t heap_max_bytes;
    Z42WriteSink stdout_sink;
    Z42WriteSink stderr_sink;
    void*  sink_user_data;
    const char* const* search_paths;
    /* NEW（append）: */
    Z42ZpkgResolverFn zpkg_resolver;
    void* zpkg_resolver_user_data;
} Z42HostConfig;
```

由于 Tier 1 已经按 `abi_version`-aware 大小读取 `Z42HostConfig`（[interop §3.3](../../../design/language/interop.md) 借用规则），append 新字段：

- 新版 host 库 + 旧版 caller → caller struct 不含新字段，runtime sizeof 检查发现 cfg 太短 → 旧版字段读出来 + 新字段当 `0` / `NULL`
- 旧版 host 库 + 新版 caller → ABI version 不变所以仍能加载；新版 caller 设了 `zpkg_resolver` 也无害（旧 runtime 不读它）

→ **ABI version 保持 1**。

## D6. 测试策略

新增 `host_tests::`：

| 测试 | 验证点 |
|------|--------|
| `resolver_via_map_resolver_loads_corelib_without_search_paths` | Tier 2 trait 端到端：用 `MapResolver` 喂 corelib + io zpkg 跑 `Hello.Main`；不设 `search_paths` |
| `resolver_via_c_hook_loads_corelib` | Tier 1 C ABI 端到端：手写 `Z42ZpkgResolverFn` 模拟外部宿主 |
| `resolver_miss_falls_back_to_search_paths` | resolver 返回 0 → runtime 应继续扫 `search_paths` 找 zpkg |
| `resolver_both_unset_load_zbc_succeeds_if_zbc_self_contained` | 都不设 + .zbc 不依赖 corelib → load 仍 OK |
| `resolver_corelib_miss_then_console_writeline_fails_at_invoke` | 都 miss + .zbc 调 Console.WriteLine → invoke 抛 VmException "undefined function" |

新 fixture：复用 `tests/data/embedding_hello/source.z42`；额外不需要新 .zbc。

## D7. 与 ABI evolution 规则的关系

`z42_host.h` 顶部已声明：
> `abi_version` MUST stay at offset 0 across all versions.
> New struct fields are appended only; layout never reordered.

本 spec 严格遵守：仅在末尾追加两个字段（fn ptr + user_data），不改顺序，不改任何已有字段。

## H1 不做的（明确）

- 异步 resolver（`async fn resolve`）— 0.8.x async/await 一起设计
- ResolverChain trait（多个 resolver 串联）— 由宿主用 `MapResolver` + 自定义 fallback 即可
- 加密 / 签名校验 hooks — 0.2.0 zbc 格式冻结再说
- 网络下载 / cache — 1.x+
