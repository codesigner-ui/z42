# Tasks: Add Embedding / Hosting API

> 状态：🟢 H0 + H1 完成 / H2 待启动 | 创建：2026-05-10
>
> 本 spec 范围 = H0–H3（design + ABI scaffold + hello-world + 错误路径）。
> H4（移动平台接入） / H5（runner 重构）由各自 spec 主导。

## 进度概览

- [x] **H0** 设计文档与 spec/changes 四件套
- [x] **H1** C ABI scaffold + Rust 单实例 state + 链接通
- [ ] **H2** Hello-world：load_zbc / resolve_entry / invoke 全链路 + Tier 2 Rust + C/Rust example
- [ ] **H3** 错误路径全覆盖 + VM exception 翻译

---

## H0: 设计文档与 spec/changes

- [x] 0.1 [docs/design/embedding.md](../../../docs/design/embedding.md) DRAFT
- [x] 0.2 [spec/changes/add-embedding-api/proposal.md](proposal.md)
- [x] 0.3 [spec/changes/add-embedding-api/design.md](design.md)
- [x] 0.4 [spec/changes/add-embedding-api/tasks.md](tasks.md)
- [x] 0.5 [spec/changes/add-embedding-api/specs/embedding-host-api/spec.md](specs/embedding-host-api/spec.md)
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
- [x] 1.4.3 [docs/design/vm-architecture.md](../../../docs/design/vm-architecture.md) 加 "Embedding Entry" 小节
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

- [ ] 2.1.1 `load_zbc`：调 `metadata::ZbcReader` 解析字节，登记到 `HostState::modules`
- [ ] 2.1.2 `resolve_entry`：解析 FQN（"namespace.Type::method"），匹配 module 元数据，登记到 `HostState::entries`
- [ ] 2.1.3 `invoke`：构造 frame、推参、调 `interp::run_method`、收返回值

### 2.2 stdout sink 接入 VM

- [ ] 2.2.1 包装 sink 回调成 `impl Write`
- [ ] 2.2.2 在 `interp::run_method` 启动前 swap stdout writer
- [ ] 2.2.3 调用结束 / shutdown 时复原

### 2.3 Tier 2 Rust crate

- [ ] 2.3.1 [src/toolchain/host/embed/Cargo.toml](../../../src/toolchain/host/embed/Cargo.toml) — crate `z42-host`，依赖 `z42-runtime`
- [ ] 2.3.2 [src/toolchain/host/embed/src/lib.rs](../../../src/toolchain/host/embed/src/lib.rs) — `Host` / `HostConfig` / `Module` / `Entry` / `Value` 安全封装

### 2.4 Examples

- [ ] 2.4.1 [src/toolchain/host/examples/hello_c/main.c](../../../src/toolchain/host/examples/hello_c/main.c)
- [ ] 2.4.2 [src/toolchain/host/examples/hello_c/CMakeLists.txt](../../../src/toolchain/host/examples/hello_c/CMakeLists.txt)
- [ ] 2.4.3 [src/toolchain/host/examples/hello_rust/Cargo.toml](../../../src/toolchain/host/examples/hello_rust/Cargo.toml)
- [ ] 2.4.4 [src/toolchain/host/examples/hello_rust/src/main.rs](../../../src/toolchain/host/examples/hello_rust/src/main.rs)
- [ ] 2.4.5 [examples/hello.z42](../../../examples/hello.z42) — 若已存在则验证签名兼容
- [ ] 2.4.6 端到端：编译 hello.z42 → hello.zbc → C / Rust 宿主跑通 → stdout 收到 "Hello, World!"

### 2.5 验证

- [ ] 2.5.1 `host_tests::load_invoke_hello_world` 通过（Rust）
- [ ] 2.5.2 hello_rust example `cargo run` 输出 "Hello, World!"
- [ ] 2.5.3 hello_c example 链接通过并运行成功

---

## H3: 错误路径全覆盖

- [ ] 3.1 `host_tests::load_zbc_bad_magic_returns_bad_zbc`
- [ ] 3.2 `host_tests::resolve_entry_unknown_returns_not_found`
- [ ] 3.3 `host_tests::invoke_arg_count_mismatch_returns_arg_mismatch`
- [ ] 3.4 `host_tests::z42_throw_returns_vm_exception_with_message`
- [ ] 3.5 `host_tests::sink_called_in_correct_order`
- [ ] 3.6 文档同步：[docs/design/embedding.md](../../../docs/design/embedding.md) §10 错误处理段补充实施细节

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
