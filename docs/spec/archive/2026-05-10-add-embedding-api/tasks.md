# Tasks: Add Embedding / Hosting API

> 状态：🟢 H0 + H1 + H2 + H3 完成 | 创建：2026-05-10 | spec 范围全部落地
>
> 本 spec 范围 = H0–H3（design + ABI scaffold + hello-world + 错误路径）。
> H4（移动平台接入） / H5（runner 重构）由各自 spec 主导。

## 进度概览

- [x] **H0** 设计文档与 spec/changes 四件套
- [x] **H1** C ABI scaffold + Rust 单实例 state + 链接通
- [x] **H2-core** load_zbc / resolve_entry / invoke 全链路 + stdout sink 接 VM + Rust 集成测试 hello-world
- [x] **H2b** Tier 2 Rust crate `z42-host` + `examples/hello_rust` 端到端 + `examples/hello_c` 参考源码
- [x] **H3** 错误路径全覆盖（5 类错误：BadZbc / EntryNotFound / ArgMismatch / VmException / sink ordering）+ classify 机制对齐 `format_uncaught`

---

## H0: 设计文档与 spec/changes

- [x] 0.1 [docs/design/runtime/embedding.md](../../../docs/design/runtime/embedding.md) DRAFT
- [x] 0.2 [docs/spec/changes/add-embedding-api/proposal.md](proposal.md)
- [x] 0.3 [docs/spec/changes/add-embedding-api/design.md](design.md)
- [x] 0.4 [docs/spec/changes/add-embedding-api/tasks.md](tasks.md)
- [x] 0.5 [docs/spec/changes/add-embedding-api/specs/embedding-host-api/spec.md](specs/embedding-host-api/spec.md)
- [x] 0.6 [docs/roadmap.md](../../../docs/roadmap.md) L2 进度表加 Embedding 行（H0 已完成 / H1–H3 待）

---

## H1: C ABI Scaffold + Rust 单实例 state

### 1.1 C 头文件

- [x] 1.1.1 [src/runtime/include/z42_host.h](../../../src/runtime/include/z42_host.h) 创建
  - 句柄：`Z42HostRef` / `Z42ModuleRef` / `Z42EntryRef`
  - 配置：`Z42HostConfig` / `Z42WriteSink` / `Z42ExecMode`
  - 状态码：`Z42HostStatus`
  - 函数：`z42_host_initialize` / `load_zbc` / `resolve_entry` / `invoke` / `set_stdout_sink` / `set_stderr_sink` / `last_error` / `shutdown`
  - 版本宏：`#define Z42_HOST_ABI_VERSION 1`
  - 头部 include：`#include "z42_abi.h"` 复用 `Z42Value` / `Z42Args` / `Z42Error`

### 1.2 Rust host 模块

- [x] 1.2.1 [src/runtime/src/host/mod.rs](../../../src/runtime/src/host/mod.rs) — 模块入口 + extern "C" 导出 + `catch_unwind` 包装
- [x] 1.2.2 [src/runtime/src/host/config.rs](../../../src/runtime/src/host/config.rs) — `Z42HostConfig` 校验、ABI version check
- [x] 1.2.3 [src/runtime/src/host/state.rs](../../../src/runtime/src/host/state.rs) — `RwLock<Option<HostState>>` 单实例
- [x] 1.2.4 [src/runtime/src/host/module.rs](../../../src/runtime/src/host/module.rs) — `Z42Module` 占位 ZST（slab 推迟到 H2 与真实 .zbc 加载一并落地）
- [x] 1.2.5 [src/runtime/src/host/entry.rs](../../../src/runtime/src/host/entry.rs) — `Z42Entry` 占位 ZST（同 1.2.4）
- [x] 1.2.6 [src/runtime/src/host/error.rs](../../../src/runtime/src/host/error.rs) — `Z42HostStatus` enum + TLS `last_error`

### 1.3 单元测试

- [x] 1.3.1 [src/runtime/src/host/host_tests.rs](../../../src/runtime/src/host/host_tests.rs)（12 个测试，含 spec 强制的 8 个 + 4 个 H1 补充覆盖）
  - `initialize_then_shutdown` ✅
  - `initialize_twice_returns_already_init` ✅
  - `shutdown_then_reinitialize` ✅
  - `shutdown_when_not_initialized_returns_not_init` ✅
  - `null_config_returns_bad_config` ✅
  - `bad_abi_version_returns_bad_config` ✅
  - `last_error_clears_on_success` ✅
  - `last_error_persists_on_failure` ✅
  - `unknown_exec_mode_returns_bad_config` ✅
  - `jit_mode_when_feature_off_returns_feature_off` ✅
  - `load_zbc_before_init_returns_not_init` ✅
  - `load_zbc_after_init_returns_internal_h2_placeholder` ✅

### 1.4 集成

- [x] 1.4.1 [src/runtime/src/lib.rs](../../../src/runtime/src/lib.rs) 加 `pub mod host;`
- [x] 1.4.2 [src/toolchain/host/README.md](../../../src/toolchain/host/README.md) 更新到 H1 状态
- [x] 1.4.3 [docs/design/runtime/vm-architecture.md](../../../docs/design/runtime/vm-architecture.md) 加 "Embedding Entry" 小节
- [x] 1.4.4 [src/runtime/include/README.md](../../../src/runtime/include/README.md) 加 `z42_host.h` 条目

### 1.5 验证

- [x] 1.5.1 `cargo build --manifest-path src/runtime/Cargo.toml` 通过
- [x] 1.5.2 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features interp-only` 通过
- [x] 1.5.3 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features ios` 通过
- [x] 1.5.4 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features android` 通过
- [x] 1.5.5 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（host:: 12 / 12，整体 322+ pass / 0 fail，含 pre-existing）

---

## H2: Hello World

### 2.1 .zbc 加载与入口解析

- [x] 2.1.1 `load_zbc`：调 `metadata::load_artifact_from_bytes` 解析字节；按 main.rs 的 eager 模式合并 corelib + 用户 import_namespaces 命中的所有 zpkg；登记 `HostState::modules`
- [x] 2.1.2 `resolve_entry`：FQN 通过 `module.func_index` 查找；接受 `namespace.Type.method` 与 `namespace.Type::method` 两种形式；登记 `HostState::entries`
- [x] 2.1.3 `invoke`：marshal `Z42Value` args → `Value`；调 `interp::run_returning`；marshal 返回值

### 2.2 stdout sink 接入 VM

> 实施期机制调整（spec design.md D4）：原计划"包装为 `impl Write` 在 `interp::run_method` 启动前 swap stdout writer"。**实际实现**：在 `corelib/io.rs` 加 `RwLock<Option<HostSink>>` 进程级 sink + `thread_local Cell<bool>` per-thread "active" flag；`route_stdout` / `route_stderr` 命中 active 线程时优先派发到 host sink。RAII `HostSinkGuard` 在 `invoke` 入口设置 active=true，离开时恢复，panic / throw 一并安全清理。C ABI 形态不变；调整为内部机制更贴合 z42 stdout 现状（thread-local stack of `Vec<u8>`）。

- [x] 2.2.1 `corelib/io.rs` 新增 `HostSink` + `HOST_STDOUT_SINK` / `HOST_STDERR_SINK` (`RwLock<Option<HostSink>>`) + `HOST_SINK_ACTIVE` thread-local flag
- [x] 2.2.2 `route_stdout` / `route_stderr` 在 active 时优先派发到 host sink；否则走原有 test sink stack / println fallback
- [x] 2.2.3 `host::ops::HostSinkGuard` RAII 包裹 `interp::run_returning`；shutdown 时清空 sink slot

### 2.3 Tier 2 Rust crate

- [x] 2.3.1 [src/toolchain/host/embed/Cargo.toml](../../../src/toolchain/host/embed/Cargo.toml) — crate `z42-host`，path-dep on `z42_vm` + `z42-abi`
- [x] 2.3.2 [src/toolchain/host/embed/src/lib.rs](../../../src/toolchain/host/embed/src/lib.rs) — `Host` / `HostConfig` / `Module` / `Entry` / `Value` 安全封装；`Drop` 自动 shutdown；sink 走 `Box<dyn Fn>` + trampoline

### 2.4 Examples

- [x] 2.4.3 [src/toolchain/host/examples/hello_rust/Cargo.toml](../../../src/toolchain/host/examples/hello_rust/Cargo.toml)
- [x] 2.4.4 [src/toolchain/host/examples/hello_rust/src/main.rs](../../../src/toolchain/host/examples/hello_rust/src/main.rs) — 端到端跑通，stdout 加 `[host]` 前缀验证
- [x] 2.4.5 [src/runtime/tests/data/embedding_hello/source.z42](../../../src/runtime/tests/data/embedding_hello/source.z42) — 集成测试 fixture
- [x] 2.4.6 端到端 Rust 集成测试 + 命令行 example
- [x] 2.4.7 [src/toolchain/host/examples/hello_c/main.c](../../../src/toolchain/host/examples/hello_c/main.c) — C 参考源码（`-fsyntax-only` 通过；desktop staticlib build 留 H4 一并做，README 解释原因）

### 2.5 验证

- [x] 2.5.1 `host::host_tests::load_invoke_hello_world` 通过（gated on `cfg(z42_have_embedding_hello)`，build.rs 自动编译 fixture）
- [x] 2.5.2 hello_rust example `cargo run` 输出 `[host] Hello, World!`
- 🔵 2.5.3 hello_c example 链接通过并运行成功 → **H4 桌面 staticlib build 一并做**（spec design 期推迟）

### 2.6 H2 范围内的实施期 Deferred

- 字符串 / 对象 / 数组 / pinned view / typeref `Z42Value` marshal —— H2 仅支持 null / i64 / f64 / bool 进出（hello-world `Main()` 无参数 + void 返回足够）；string 入参的 `pinned` 协议留 H3
- 多 zpkg 懒加载（`declared_candidates` 非空）—— H2 走 eager merge 避免 invoke 时的"惊讶懒查"；多 zpkg lazy 留 H3
- **桌面 hello_c staticlib build pipeline** —— `z42_vm` crate 当前只产 `rlib`；要让 C 程序链上去需要 `staticlib` / `cdylib` + `cargo rustc --print=native-static-libs` 的全套系统库。iOS / Android spec（H4）必须把这套配好；为避免桌面 + 移动重复两份配置，hello_c 的实际链接 + 跑通跟 H4 一起做。当前 hello_c `main.c` 已写完且 `gcc -fsyntax-only` 通过

---

## H3: 错误路径全覆盖

- [x] 3.1 `host_tests::load_zbc_with_garbage_bytes_returns_bad_zbc`（H2 已就位，H3 复用）
- [x] 3.2 `host_tests::resolve_entry_unknown_fqn_returns_entry_not_found`
- [x] 3.3 `host_tests::invoke_arg_count_mismatch_returns_arg_mismatch`（fixture `Main()` 0 参 vs 传 1 个 I64）
- [x] 3.4 `host_tests::z42_throw_escapes_as_vm_exception_with_message`（fixture `Boom()` `throw new Exception(...)`）
- [x] 3.5 `host_tests::sink_called_in_correct_order_for_multiple_lines`（fixture `MultiLine()` 3 行）
- [x] 3.6 [docs/design/runtime/embedding.md](../../../docs/design/runtime/embedding.md) §10 状态码 → 触发条件表 + 错误消息分类机制段
- [x] 3.7 [src/runtime/src/host/ops.rs](../../../src/runtime/src/host/ops.rs) `invoke_impl` 加 `args.len() != func.param_count` 检查；前缀 `arg-count-mismatch:` 用于 `classify_invoke_error` 分流
- [x] 3.8 [src/runtime/src/host/mod.rs](../../../src/runtime/src/host/mod.rs) `classify_invoke_error` 修复（`"Unhandled exception"` → `"uncaught exception"` 与 `format_uncaught` 对齐）
- [x] 3.9 [docs/spec/changes/add-embedding-api/specs/embedding-host-api/spec.md](specs/embedding-host-api/spec.md) 加 Requirement"Hello-World 端到端" + Requirement"错误路径分类（H3）"

---

## 风险与备注

### 工作量估计

- H1：1–2 天（scaffold + 8 个 unit test）
- H2：2–3 天（重头：sink 接入 VM、FQN 解析、example 链接）
- H3：0.5–1 天（补全错误路径测试）

### 实施依赖

- 必须先完成 M4（interpreter）—— ✅ 已完成
- 不依赖 M9（AOT）；H1–H3 仅走 interp 路径

### 与并行 spec 的协调

- **add-platform-ios / add-platform-android**：H4 阶段，本 spec 完成后启动；它们的 C bridge 会从手写 `z42_ios.h` 改为直接复用 `z42_host.h`
- **rewrite-z42-test-runner-compile-time**：H5 阶段，runner library 内部从直连 VM 改为基于 `z42-host` crate
