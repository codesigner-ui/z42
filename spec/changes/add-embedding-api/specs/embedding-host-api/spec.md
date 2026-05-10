# Spec: Embedding / Hosting API

## ADDED Requirements

### Requirement: 设计文档

#### Scenario: docs/design/embedding.md 存在并覆盖核心章节

- **WHEN** 阅读 [docs/design/embedding.md](../../../../docs/design/embedding.md)
- **THEN** 含 §1 设计目标、§2 设计原则、§3 架构、§4 Tier 1 C ABI、§7 生命周期、§9 Hello World、§12 Deferred 章节
- **AND** §1 明确指出与 [interop.md](../../../../docs/design/interop.md) 的边界：interop = native 注册类型；embedding = 宿主启动 VM

---

### Requirement: C ABI 头文件

#### Scenario: z42_host.h 存在并 include z42_abi.h

- **WHEN** 阅读 [src/runtime/include/z42_host.h](../../../../src/runtime/include/z42_host.h)
- **THEN** 文件第一段 `#include "z42_abi.h"` 复用其类型定义
- **AND** 不重复定义 `Z42Value` / `Z42Args` / `Z42Error`

#### Scenario: 必要句柄类型

- **WHEN** 阅读 z42_host.h
- **THEN** 含 `typedef struct Z42Host* Z42HostRef;`
- **AND** 含 `typedef struct Z42Module* Z42ModuleRef;`
- **AND** 含 `typedef struct Z42Entry* Z42EntryRef;`

#### Scenario: 配置结构

- **WHEN** 阅读 z42_host.h
- **THEN** 含 `Z42HostConfig` struct，第一字段是 `uint32_t abi_version`，offset 0
- **AND** 含 `Z42WriteSink` 函数指针 typedef，签名 `void (*)(const char*, size_t, void*)`
- **AND** 含 `Z42ExecMode` enum，值 `DEFAULT=0 / INTERP=1 / JIT=2 / AOT=3`
- **AND** 含 `#define Z42_HOST_ABI_VERSION 1`

#### Scenario: 状态码

- **WHEN** 阅读 z42_host.h
- **THEN** 含 `Z42HostStatus` enum
- **AND** 含 `OK=0 / ERR_ALREADY_INIT=1 / ERR_NOT_INIT=2 / ERR_BAD_CONFIG=3 / ERR_FEATURE_OFF=4`
- **AND** 含 `ERR_BAD_ZBC=10 / ERR_VERIFICATION=11`
- **AND** 含 `ERR_ENTRY_NOT_FOUND=20 / ERR_ARG_MISMATCH=21`
- **AND** 含 `ERR_VM_EXCEPTION=30 / ERR_INTERNAL=99`

#### Scenario: 函数声明

- **WHEN** 阅读 z42_host.h
- **THEN** 含函数 `z42_host_initialize` / `z42_host_load_zbc` / `z42_host_resolve_entry` / `z42_host_invoke` / `z42_host_set_stdout_sink` / `z42_host_set_stderr_sink` / `z42_host_last_error` / `z42_host_shutdown`
- **AND** 全部 `extern "C"`-friendly（C 头文件天然如此）

---

### Requirement: Rust host 模块

#### Scenario: 模块入口存在

- **WHEN** 阅读 [src/runtime/src/host/mod.rs](../../../../src/runtime/src/host/mod.rs)
- **THEN** 声明子模块 `config` / `state` / `module` / `entry` / `error`
- **AND** 导出全部 `extern "C" fn z42_host_*` 函数

#### Scenario: 单实例 state 管理

- **WHEN** 阅读 [src/runtime/src/host/state.rs](../../../../src/runtime/src/host/state.rs)
- **THEN** 用 `RwLock<Option<HostState>>` 承载单实例（不用 `OnceCell`，因为需要支持 shutdown）

#### Scenario: lib.rs 导出 host 模块

- **WHEN** 阅读 [src/runtime/src/lib.rs](../../../../src/runtime/src/lib.rs)
- **THEN** 含 `pub mod host;`

---

### Requirement: 生命周期语义

#### Scenario: initialize 成功创建 host state

- **WHEN** 调用 `z42_host_initialize` 传入合法 `Z42HostConfig`
- **THEN** 返回 `Z42_HOST_OK`
- **AND** `*out_host` 被赋值为非 NULL handle

#### Scenario: 重复 initialize 返回 ALREADY_INIT

- **GIVEN** `z42_host_initialize` 已成功调用
- **WHEN** 再次调用 `z42_host_initialize`
- **THEN** 返回 `Z42_HOST_ERR_ALREADY_INIT`

#### Scenario: shutdown 后可再 initialize

- **GIVEN** `z42_host_initialize` 成功后 `z42_host_shutdown` 也成功
- **WHEN** 再次调用 `z42_host_initialize`
- **THEN** 返回 `Z42_HOST_OK`

#### Scenario: 未 initialize 时 shutdown 返回 NOT_INIT

- **GIVEN** 进程从未调用过 `z42_host_initialize`
- **WHEN** 调用 `z42_host_shutdown`
- **THEN** 返回 `Z42_HOST_ERR_NOT_INIT`

#### Scenario: NULL config 返回 BAD_CONFIG

- **WHEN** 调用 `z42_host_initialize(NULL, &handle)`
- **THEN** 返回 `Z42_HOST_ERR_BAD_CONFIG`

#### Scenario: 错误 abi_version 返回 BAD_CONFIG

- **WHEN** 调用 `z42_host_initialize` 传入 `cfg.abi_version != Z42_HOST_ABI_VERSION`
- **THEN** 返回 `Z42_HOST_ERR_BAD_CONFIG`

#### Scenario: feature 关闭时请求 JIT 返回 FEATURE_OFF

- **GIVEN** runtime 编译时 `--no-default-features --features interp-only`
- **WHEN** 调用 `z42_host_initialize` 传入 `cfg.exec_mode = Z42_EXEC_MODE_JIT`
- **THEN** 返回 `Z42_HOST_ERR_FEATURE_OFF`

---

### Requirement: 错误信息 TLS

#### Scenario: 成功调用清空 last_error

- **GIVEN** 之前一次调用失败设置了 last_error
- **WHEN** 任意 host API 调用成功返回 `Z42_HOST_OK`
- **THEN** `z42_host_last_error` 返回 `code == 0`

#### Scenario: 失败调用保留 last_error

- **GIVEN** `z42_host_initialize` 因 BAD_CONFIG 失败
- **WHEN** 立即在同线程调用 `z42_host_last_error`
- **THEN** 返回非零 code 与非空 message

---

### Requirement: H1 占位行为

> H1 阶段未实现的功能必须返回 `ERR_INTERNAL`，message 含 "H2 not yet implemented"，
> 而非 panic 或 segfault。这是为 H2 留接口的契约。

#### Scenario: H1 阶段 load_zbc 返回 INTERNAL

- **GIVEN** H1 完成、H2 未启动
- **WHEN** 调用 `z42_host_load_zbc` 传入合法字节流
- **THEN** 返回 `Z42_HOST_ERR_INTERNAL`
- **AND** `last_error.message` 含 "H2"

> 注：H2 完成后本 Scenario 删除，替换为 [Hello World 端到端] Requirement。

---

### Requirement: 构建矩阵

#### Scenario: 默认 features 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml`
- **THEN** 返回 0

#### Scenario: interp-only 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features interp-only`
- **THEN** 返回 0

#### Scenario: ios feature 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features ios`
- **THEN** 返回 0

#### Scenario: android feature 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features android`
- **THEN** 返回 0

---

### Requirement: 测试覆盖

#### Scenario: H1 unit 测试存在

- **WHEN** 阅读 [src/runtime/src/host/host_tests.rs](../../../../src/runtime/src/host/host_tests.rs)
- **THEN** 至少含以下测试名：
  - `initialize_then_shutdown`
  - `initialize_twice_returns_already_init`
  - `shutdown_then_reinitialize`
  - `shutdown_when_not_initialized_returns_not_init`
  - `null_config_returns_bad_config`
  - `bad_abi_version_returns_bad_config`
  - `last_error_clears_on_success`
  - `last_error_persists_on_failure`

#### Scenario: 全部测试通过

- **WHEN** 执行 `cargo test --manifest-path src/runtime/Cargo.toml`
- **THEN** 返回 0；含 pre-existing 测试均通过

---

### Requirement: 文档同步

#### Scenario: src/toolchain/host/README.md 反映状态

- **WHEN** 阅读 [src/toolchain/host/README.md](../../../../src/toolchain/host/README.md)
- **THEN** 不再含 "尚未实现"
- **AND** 列出 H1 已落地、H2 / H3 待实施

#### Scenario: vm-architecture.md 含 Embedding 入口小节

- **WHEN** 阅读 [docs/design/vm-architecture.md](../../../../docs/design/vm-architecture.md)
- **THEN** 含 "Embedding Entry" 章节
- **AND** 描述 `host` 模块如何挂接到 VM 全局状态（stdout sink、shutdown 时机）

#### Scenario: roadmap.md 反映 Embedding 进度

- **WHEN** 阅读 [docs/roadmap.md](../../../../docs/roadmap.md)
- **THEN** L2 进度表含 Embedding API 行，标 H0 完成 / H1–H3 待
