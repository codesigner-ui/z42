# Design: 以 zpkg 自描述取代 index.json

## Architecture

唯一原则：**zpkg 的 `NSPC` section 是 namespace 归属的唯一真相源。namespace → 文件/字节
的映射只能扫 NSPC 派生，绝不手写。** 解析机制只有一个，按平台不同的只是"zpkg 字节从哪来"。

```
                    ┌─────────────────────────────────────────┐
   字节来源（平台异）│  桌面: 扫 search_paths/*.zpkg（发现式）   │
                    │  移动/WASM: 宿主 z42_host_add_zpkg 注入   │
                    └────────────────────┬────────────────────┘
                                         │  (统一)
                                         ▼
                    runtime 对每块字节读 NSPC → namespace → module 索引
                                         │
                                         ▼
              load_zbc: 按 import_namespaces 查索引，merge 进 user module
```

旧 pull 模型（`resolve(ns) -> bytes`，宿主自带 ns→bytes 预言机）被删除 —— 它是 index.json
的根源。

## Decisions

### Decision 1: push 注入取代 pull resolver

**问题：** 移动/WASM 如何把 stdlib zpkg 交给 runtime？
**选项：**
- A（pull，现状）：runtime 调 `resolver.resolve(ns)`，宿主答字节 → 宿主必须知道 ns→文件 → 需 index.json。
- B（push，本设计）：宿主 `z42_host_add_zpkg(bytes)` 注入；runtime 读 NSPC 自己认领 ns。宿主零映射知识。

**决定：** 选 B 并**删除** A。pre-1.0 不留兼容（philosophy.md「不为旧版本提供兼容」）。
注入字节在 `HostState` 内累积，`z42_host_load_zbc` 时已建好 `namespace → Module` 索引。

### Decision 2: 解析顺序

`build_host_module` 对每个 namespace（`["z42.core"] + user.import_namespaces`）按序：

1. **注入索引**（来自 `z42_host_add_zpkg` 的字节，已读 NSPC）→ 命中用该 module
2. **corelib**（`z42.core` 的已知路径，桌面 corelib）
3. **桌面 search_paths 扫描**（`resolve_namespace` 读 NSPC）→ 命中加载
4. **silent miss**：load 仍 OK，invoke 时报 "undefined function"（保持现有语义）

> 注入索引与 search_paths 互不排斥：注入优先，未注入的 namespace 仍可被桌面扫描兜底。

### Decision 3: 注入索引以 module 还是 bytes 缓存

**决定：** 注入时**立即** `load_artifact_from_bytes` 解析为 `Module` 并记录其 `NSPC`
namespace 列表，存 `Vec<(namespaces, Module)>`。理由：注入发生在 load 前，eager 解析一次即可，
避免 load 时重复 parse；与现有 "merge 进 user module" 流程对接最直接。

### Decision 4: NSPC 读取复用

**决定：** Rust 侧复用既有 `metadata::zbc_reader::read_zpkg_namespaces(bytes)`
（[zbc_reader.rs:1505](../../../../src/runtime/src/metadata/zbc_reader.rs)）。WASM JS 侧通过
新增 wasm-bindgen 导出 `read_namespaces(bytes) -> string[]`（薄封装同一函数）让 JS resolver
不必重写 zpkg 解析。**无需新增 z42c CLI 子命令**（因 stdlib.lock 已 Out of Scope）。

### Decision 5: 浏览器 WASM 的枚举替身

**问题：** 浏览器经 HTTP 无法 `readdir` stdlib 目录。
**决定：** `build.sh` 在把 zpkg 拷进 `js/stdlib/` 时，顺手生成一份**文件名清单**
（如 `stdlib/files.json` = `["z42.core.zpkg", ...]`，纯文件名数组，**非** namespace 映射）。
浏览器 JS fetch 清单 → 逐个 fetch zpkg → `read_namespaces` 认领。Node 端直接 `readdirSync`，
不需清单。

> 文件名清单是 build 一次性 `ls` 产物，零手维护，且不含会漂移的 namespace 映射 —— 与"消除
> index.json"目标一致。它**不**是 stdlib.lock（版本/完整性那套仍 Deferred）。

### Decision 6: 确定性

注入索引构建、search_paths 扫描、文件名清单生成，凡 first-wins 注册前一律按文件名
`Ordinal` 排序（[common-pitfalls §1](../../../../.claude/rules/common-pitfalls.md)），保证多包共享
namespace（如 z42.core 提供 Std/Std.Exceptions）时跨平台行为一致。

## Implementation Notes

- `Z42HostConfig` 删 `zpkg_resolver` / `zpkg_resolver_user_data` 两字段；新增注入走独立 API
  调用而非 config（注入可在 init 后、load 前多次）。
- `HostState` 加 `injected: Vec<InjectedZpkg>` 字段（namespaces + Module）。
- `merge_modules` / `build_type_registry` 等下游不变，只是 module 来源多了"注入"一路。
- WASM `src/resolver.rs` 的 `JsCallbackResolver`（pull）整体替换为注入路径。
- 删除 `install_zpkg_resolver`（mod.rs）及其 Rust Tier-2 用例。

## Testing Strategy

- **Rust 单元**（`host/inject_tests.rs`）：
  - 注入一个多 namespace zpkg（z42.core），`Std.Exceptions` 与 `Std` 都解析到同一 module。
  - 注入顺序打乱 → 索引结果一致（确定性）。
  - 未注入且无 search_paths → silent miss，load OK、invoke 报错。
- **VM e2e**（`src/tests/host/inject-multi-namespace/`）：一个 import `Std.IO` 的 .zbc 经注入
  z42.io 后正确运行。
- **WASM**：更新 `tests/r1-r7.spec.ts` / `tests/host.js`，验证读 NSPC 路径取代 index.json 后
  Node + 浏览器均能解析。
- **GREEN gate**：`z42 xtask.zpkg test`（含 dist：发行接口变更需 `test dist`）。
