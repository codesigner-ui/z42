# Proposal: Add ZpkgResolver Hook to Embedding API

## Why

[`z42-host`](../../../../src/toolchain/host/embed/) 当前**只能通过 `search_paths` 在文件系统里扫 zpkg**。这对桌面够用，但对 H4 三个移动 / Web 平台是死结：

- **iOS / Android**：app bundle 内的资源不是普通 fs 路径；要从 `Bundle.main` / `AssetManager` 读取
- **WASM**：浏览器没有文件系统；Node WASI 有但局限；常规姿势是 JS 端 `fetch` 拿到 `Uint8Array` 后喂进来
- **iOS 真机 + Android 沙箱**：即使能造路径，把 zpkg 解压到磁盘是不必要的 IO 开销

H4 spec 落地前必须先开这个口子。否则 H4 会被迫往 `z42-host` 里塞 platform-specific 入口（破坏"三层 ABI"的统一性）。

## What Changes

新增 **ZpkgResolver hook**：宿主提供一个回调，运行时按需问"namespace X 的 zpkg 字节"。

三层贯通：

1. **Tier 1 C ABI**（[`z42_host.h`](../../../../src/runtime/include/z42_host.h)）：
   - `typedef int (*Z42ZpkgResolverFn)(const char*, const uint8_t**, size_t*, void*)`
   - `Z42HostConfig` 末尾**追加** `zpkg_resolver` + `zpkg_resolver_user_data` 字段（append-only，ABI version **不动**）
2. **Tier 2 Rust**（[`z42-host`](../../../../src/toolchain/host/embed/)）：
   - `pub trait ZpkgResolver: Send + Sync { fn resolve(&self, namespace: &str) -> Option<Vec<u8>>; }`
   - `HostConfig::zpkg_resolver: Option<Arc<dyn ZpkgResolver>>`
   - 内置实现：`SearchPathsResolver`（包当前 `search_paths` 行为）、`MapResolver`（HashMap-based eager 模式）
3. **Runtime 集成**（[`src/runtime/src/host/ops.rs`](../../../../src/runtime/src/host/ops.rs) `build_host_module`）：
   - 解析 user `.zbc` → 提取 `import_namespaces` + 默认始终探 `z42.core`
   - 对每个 namespace 优先调 resolver hook；miss 后 fallback 扫 `search_paths`（向后兼容）
   - 两条路径加载到的 `Module` 走同一 `merge_modules` 流程

无新 IR opcode、无新 z42 语法。**纯 host API 扩展**。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/include/z42_host.h` | MODIFY | append `Z42ZpkgResolverFn` typedef + `Z42HostConfig` 两个字段 |
| `src/runtime/src/host/config.rs` | MODIFY | `Z42HostConfig` C 镜像同步；`ResolvedConfig` 加 resolver 字段 |
| `src/runtime/src/host/state.rs` | MODIFY | `HostState` 持 `Option<Arc<dyn ZpkgResolver>>` |
| `src/runtime/src/host/resolver.rs` | NEW | `ZpkgResolver` trait + `SearchPathsResolver` / `MapResolver` 内置实现 + C hook adapter |
| `src/runtime/src/host/ops.rs` | MODIFY | `build_host_module` 改造：resolver 优先 + search_paths fallback |
| `src/runtime/src/host/mod.rs` | MODIFY | `pub mod resolver;`；`set_zpkg_resolver` 不暴露 C API（仅 Tier 2 用）；config 解析路径加 resolver |
| `src/runtime/src/host/host_tests.rs` | MODIFY | 新增 3 个 resolver 测试：trait impl + C hook adapter + search_paths fallback |
| `src/runtime/src/host/host_tests.rs` 集成测试 fixture | MODIFY | `load_invoke_hello_world` 加一个变体：用 `MapResolver` 不带 search_paths 跑通 |
| `src/toolchain/host/embed/src/lib.rs` | MODIFY | `HostConfig::zpkg_resolver`；公开 `ZpkgResolver` trait + 内置 resolver；examples 不动 |
| `src/toolchain/host/examples/hello_rust/src/main.rs` | MODIFY | 加可选 `--resolver map` flag 演示 |
| `docs/design/runtime/embedding.md` | MODIFY | §4.2 `Z42HostConfig` 加 resolver 字段；新增 §11 ZpkgResolver 节 |
| `src/runtime/include/README.md` | MODIFY | 头文件 surface 更新 |
| `src/toolchain/host/README.md` | MODIFY | Tier 2 API 增量 |
| `src/toolchain/host/platforms/README.md` | NEW（已建）/ MODIFY | 平台 facade 默认 resolver 表格指向本 spec |

**只读引用**：
- `src/runtime/src/metadata/loader.rs` — `load_artifact_from_bytes` 已支持 in-memory
- `src/runtime/src/metadata/merge.rs` — `merge_modules` 不变
- `docs/spec/archive/2026-05-10-add-embedding-api/` — 上下文

## Out of Scope

- **平台 facade 实现** —— 归 `add-platform-{ios,android,wasm}/`（本 spec 是它们的**前置**）
- **异步 resolver**（如 wasm 端 fetch） —— v0.1 假定 resolver 同步返回；async 留 0.8.x async/await 一起设计
- **Resolver 链 / 多级 resolver** —— v0.1 一个 resolver；多个的合成由宿主自行做（如内置 `ChainedResolver` 留 0.5.x+）
- **资源签名 / 完整性校验** —— 留 0.2.0 zbc/zpkg 格式冻结一并处理
- **下载缓存 / 增量更新** —— 1.x+
- **HotReload 触发 zpkg 重新解析** —— 0.3.2 热重载 spec 自己解决

## Open Questions

- [ ] **Q1**：C hook 的 bytes 生命周期 —— "callback 返回前必须有效"已足够，还是要支持"runtime 持有更久"？
  - 倾向：**callback 内复制**。runtime 在 callback 内立即 `read_zbc(bytes)` 解析完，bytes 不出 callback 作用域。简单、无生命周期歧义。
- [ ] **Q2**：resolver 优先 vs search_paths 优先？
  - 倾向：**resolver 优先**。如果宿主主动设了 resolver，说明它想全权管 zpkg；search_paths 只作 fallback。
- [ ] **Q3**：是否给 namespace_name 加格式约定（"Std.IO" vs "Std::IO" vs "z42.io"）？
  - 倾向：传 z42 编译器在 `import_namespaces` 里实际写的字符串（如 `"Std.IO"`），让 host 直接 lookup；不强制转换。文档说明。
- [ ] **Q4**：corelib 探测策略 —— 总是请求 namespace `"z42.core"`？
  - 倾向：**是**。runtime 在 `build_host_module` 开头无条件 `resolver.resolve("z42.core")`；miss 不算错（让用户代码不依赖 corelib 时也能跑），但 fixture 测试覆盖"corelib 不存在则 Console.WriteLine fail at runtime"。
- [ ] **Q5**：resolver miss 是 `Z42_HOST_ERR_BAD_ZBC` 还是 silent fallback 到 search_paths？
  - 倾向：**silent fallback**。miss 不是错误，只是该 namespace 在该 resolver 不可达；后续 `load_zbc` 报"undefined function"才是真错。
