# Proposal: Add Embedding / Hosting API

## Why

z42 当前**没有宿主嵌入 API**。`src/toolchain/host/` 仅有占位 README。

[interop.md](../../../docs/design/language/interop.md) 解决的是 "native 代码 → 注册类型/方法进 z42"（类比 CPython C 扩展）；当前缺的是另一面 —— **宿主 app → 启动 VM → 加载 .zbc → 调用入口 → 关闭**（类比 CoreCLR `coreclrhost.h` / JNI `JavaVM` / Lua `lua_State`）。

没有这层 API：

- iOS / Android app 不能内嵌 z42（[cross-platform.md](../../../docs/design/runtime/cross-platform.md) 仅解决 VM 怎么编译，没解决怎么被宿主调用）
- [cross-platform-testing.md](../../../docs/design/testing/cross-platform-testing.md) 的 test-runner library 必须自己直连 VM internals，与平台 facade 共用一份调用路径困难
- 桌面 IDE 插件 / 其他原生应用没有稳定 ABI 接入

本 spec 落地 **H0–H3 阶段**（design + C ABI scaffold + hello-world + 错误路径），H4 / H5（移动平台接入、test-runner 重构）由各 P4.x / runner 自有 spec 主导。

## What Changes

- **新建 [docs/design/runtime/embedding.md](../../../docs/design/runtime/embedding.md)**（已 DRAFT）— 三层 ABI 设计、单实例语义、生命周期、错误码、Hello World 示例、Deferred 清单
- **新建 [src/runtime/include/z42_host.h](../../../src/runtime/include/z42_host.h)** — Tier 1 C ABI 头文件，与 `z42_abi.h` 平行
- **新建 [src/runtime/src/host/](../../../src/runtime/src/host/)** — Rust `extern "C"` 实现：
  - `mod.rs` — 模块入口与导出
  - `config.rs` — `Z42HostConfig` / `Z42WriteSink` 处理
  - `state.rs` — 进程级单实例 state（v0.1 用 `OnceCell` / `Mutex`）
  - `module.rs` — `.zbc` 加载与 `Z42ModuleRef` 句柄表
  - `entry.rs` — FQN 解析与 invoke
  - `error.rs` — `Z42HostStatus` ↔ Rust 错误转换、TLS `last_error`
- **新建 [src/toolchain/host/embed/](../../../src/toolchain/host/embed/)** — Tier 2 Rust crate `z42-host`（H2 引入；H1 仅占位）
- **新建 [src/toolchain/host/examples/](../../../src/toolchain/host/examples/)** — hello-world C / Rust 示例（H2 引入）
- **修改 [src/runtime/src/lib.rs](../../../src/runtime/src/lib.rs)** — `pub mod host;`
- **修改 [src/toolchain/host/README.md](../../../src/toolchain/host/README.md)** — 移除"尚未实现"，更新到 H1 状态
- **修改 [docs/roadmap.md](../../../docs/roadmap.md)** — L2 进度表加 H0–H3 行
- **修改 [docs/design/runtime/vm-architecture.md](../../../docs/design/runtime/vm-architecture.md)** — H1 实施时追加"Embedding 入口"小节

## Scope

| 文件路径 | 变更类型 | 阶段 |
|---------|---------|------|
| `docs/design/runtime/embedding.md` | NEW | H0 (已完成) |
| `docs/spec/changes/add-embedding-api/{proposal,design,tasks}.md` | NEW | H0 |
| `docs/spec/changes/add-embedding-api/specs/embedding-host-api/spec.md` | NEW | H0 |
| `src/runtime/include/z42_host.h` | NEW | H1 |
| `src/runtime/src/host/mod.rs` | NEW | H1 |
| `src/runtime/src/host/config.rs` | NEW | H1 |
| `src/runtime/src/host/state.rs` | NEW | H1 |
| `src/runtime/src/host/module.rs` | NEW | H1 |
| `src/runtime/src/host/entry.rs` | NEW | H1 |
| `src/runtime/src/host/error.rs` | NEW | H1 |
| `src/runtime/src/host/host_tests.rs` | NEW | H1 |
| `src/runtime/src/lib.rs` | MODIFY | H1 |
| `src/toolchain/host/README.md` | MODIFY | H1 |
| `src/toolchain/host/embed/Cargo.toml` | NEW | H2 |
| `src/toolchain/host/embed/src/lib.rs` | NEW | H2 |
| `src/toolchain/host/examples/hello_c/main.c` | NEW | H2 |
| `src/toolchain/host/examples/hello_rust/Cargo.toml` | NEW | H2 |
| `src/toolchain/host/examples/hello_rust/src/main.rs` | NEW | H2 |
| `docs/design/runtime/vm-architecture.md` | MODIFY | H1 |
| `docs/roadmap.md` | MODIFY | H0 |

**只读引用**：
- [src/runtime/include/z42_abi.h](../../../src/runtime/include/z42_abi.h) — 复用 `Z42Value` / `Z42Args` / `Z42Error`
- [src/runtime/src/native/](../../../src/runtime/src/native/) — 借鉴 dispatch / marshal 代码组织
- [src/runtime/src/vm.rs](../../../src/runtime/src/vm.rs) — interp 入口
- [docs/design/language/interop.md](../../../docs/design/language/interop.md) — Tier 1/2/3 分层哲学
- [docs/design/runtime/cross-platform.md](../../../docs/design/runtime/cross-platform.md) — feature flag 与 ExecMode 关系

## Out of Scope

- **多 VM 实例 / ALC-like context**：v0.1 单实例；进 [embedding.md §12 Deferred](../../../docs/design/runtime/embedding.md)
- **Hot reload**：依赖多实例
- **GC handle 暴露给宿主**：触发条件由真实需求决定
- **Async / 协程式 invoke**：等 z42 引入 async（L3）
- **从宿主 catch z42 异常对象**：v0.1 只暴露 status code + message
- **VM 内部自动 mutex**：宿主显式串行化
- **Tier 3 平台 facade（Swift / Kotlin / JS API 形态）**：归 P4.3 / P4.4 / P4.2 各自 spec
- **test-runner 重构到 host API 之上**：归 H5（独立 spec）
- **自动化 cbindgen**：手写 `z42_host.h`（API surface 小，无需引入额外依赖）

## Open Questions

- [ ] **Q1**：`Z42Value` 字符串类型是否在 v0.1 即支持？hello-world 入口签名 `(string) -> int`，需要至少能传一个 string 进去。
  - 倾向：**是**。沿用 [interop.md §6.3 pinned](../../../docs/design/language/interop.md) 模型；v0.1 host 端只支持"宿主分配的 UTF-8 buffer + 长度"形式（不直接构造 z42 GC string）。
- [ ] **Q2**：`Z42HostStatus` 与 [error-codes.md](../../../docs/design/compiler/error-codes.md) 编号空间是否冲突？
  - 倾向：**不冲突**。`Z42HostStatus` 是 ABI 层数值码（0-99），不进 Z 系列错误码命名空间。
- [ ] **Q3**：`z42_host_initialize` 多次调用语义？
  - 倾向：**返回 ERR_ALREADY_INIT**（CoreCLR 行为）。`shutdown` 后允许再 `initialize`。
- [ ] **Q4**：H1 阶段 stub 函数返回什么状态码？
  - 倾向：`ERR_NOT_INIT` / `Z42_HOST_ERR_INTERNAL`。`initialize` 在 H1 至少能成功（创建空 host state），后续 `load_zbc` 等返回 `ERR_INTERNAL` 表示"未实现"，进 H2 替换。
