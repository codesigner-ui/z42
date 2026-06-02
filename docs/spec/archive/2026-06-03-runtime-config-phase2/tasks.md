# Tasks: D1 Phase 2 — migrate subsystem-local Z42_* reads to RuntimeConfig

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor
> 来源：[`docs/review.md`](../../../review.md) Part 4 D1（Phase 1 已落地 add-runtime-config-central 2026-05-25；Phase 2 项 "migrating subsystem-local Z42_* reads still open"）

## 变更说明

把 6 个 subsystem-local `std::env::var("Z42_*")` 读 + `OnceLock` 缓存模式
迁到中心 `RuntimeConfig`：

| 当前位置 | 当前模式 | 迁移目标 |
|---|---|---|
| `gc/safepoint.rs::throttle_n` | `OnceLock<u32>` reads `Z42_SAFEPOINT_THROTTLE` | `RuntimeConfig::safepoint_throttle` |
| `gc/arc_heap.rs::minor_escalation_threshold` | `OnceLock<f32>` reads `Z42_GC_MINOR_THRESHOLD` | `RuntimeConfig::gc_minor_threshold` |
| `gc/types.rs::pause_window_cap_from_env` | bare `env::var` reads `Z42_GC_PAUSE_WINDOW` (per-call!) | `RuntimeConfig::gc_pause_window` |
| `gc/soft_registry.rs::soft_threshold_from_env` | bare `env::var` reads `Z42_GC_SOFT_THRESHOLD` (per-call!) | `RuntimeConfig::gc_soft_threshold` |
| `gc/mode.rs::GcMode::from_env` | bare `env::var` reads `Z42_GC_MODE` (per-call!) | `RuntimeConfig::gc_mode` |
| `native/ext.rs::native_search_paths` | bare `env::var` reads `Z42_NATIVE_PATH` (per-call!) | `RuntimeConfig::native_search_paths` |

新增机制：进程级 `LazyLock<RuntimeConfig>` 在 `config.rs` 暴露
`pub fn runtime_config() -> &'static RuntimeConfig`，第一次访问时从 env
解析所有 knob（一次性，warning 集中输出）。Subsystem 改为
`config::runtime_config().<field>` 读已 parsed 值。

## 原因

review.md Part 4 D1 把 P0 RuntimeConfig 中心化标 ✅，备注说 Phase 2 "still
open"。当前几个 GC 子系统仍各自 `env::var` + OnceLock，重复实现了 Phase 1
的中心化目标——`--info` 表里 `KNOWN_KNOBS` 列了它们但实际值的真相来源
仍散落。Phase 2 把它们物理迁到 RuntimeConfig，让"declared in KNOWN_KNOBS"
与"read by subsystem"是同一份代码路径。

## 文档影响

- `docs/review.md` D1 / E3 表格更新（Phase 2 done）
- `docs/design/runtime/vm-architecture.md` RuntimeConfig 节加 LazyLock 全局
  + 迁移注

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/config.rs` | MODIFY | 加 6 个新字段 + parsing in `from_getter` + `pub static RUNTIME_CONFIG: LazyLock<RuntimeConfig>` + `pub fn runtime_config() -> &'static RuntimeConfig` |
| `src/runtime/src/gc/safepoint.rs` | MODIFY | `throttle_n` 改读 `runtime_config().safepoint_throttle`，删 OnceLock |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `minor_escalation_threshold` 改读 `runtime_config().gc_minor_threshold`，删 OnceLock |
| `src/runtime/src/gc/types.rs` | MODIFY | `pause_window_cap_from_env` 改读 `runtime_config().gc_pause_window`（保留函数签名，内部换实现） |
| `src/runtime/src/gc/soft_registry.rs` | MODIFY | `soft_threshold_from_env` 同上 |
| `src/runtime/src/gc/mode.rs` | MODIFY | `GcMode::from_env` 改读 `runtime_config().gc_mode` |
| `src/runtime/src/native/ext.rs` | MODIFY | `native_search_paths` 改读 `runtime_config().native_search_paths`，省去 split logic（config 已 split） |
| `docs/review.md` | MODIFY | D1 / E3 状态更新 |
| `docs/design/runtime/vm-architecture.md` | MODIFY | RuntimeConfig 节加 Phase 2 注 |

只读引用：现有 config.rs `KNOWN_KNOBS` 表（已声明所有 knobs；本 spec 把
读路径对齐到这张表）。

## 设计要点

### LazyLock 选择

Rust 1.80+ stable `std::sync::LazyLock`（项目实测 rustc 1.88）。无外部 dep。
首次访问时 `from_env()` 一次性 parse 所有 knob，**集中输出 warning**
（vs 当前每个 subsystem 各自 eprintln）。

### Test injectability 保留

`RuntimeConfig::from_getter<F>(get: F)` API 保持原样 —— 测试可以构造独立的
`RuntimeConfig` 实例。生产路径走 `runtime_config()` 全局；测试不动全局，
直接 build 局部 `RuntimeConfig`。

### Warning 集中

当前 4 处 `eprintln!("z42: invalid Z42_X={:?}; using default Y", s)`
散落。迁移后所有 warning 在 `RuntimeConfig::from_env()` 一处输出，便于
排查 + 单一 stderr 模式。

### 不动的部分

- `Z42_STRESS_ITERS` / `Z42_STRESS_SEED` —— 测试代码 only（`gc/arc_heap_tests/stress.rs`），
  不进 RuntimeConfig（避免 production 路径背 test-only knob）
- 现有 `RuntimeConfig.libs_dir` / `module_path` / `log_filter` / `crash_dir`
  —— 已在 Phase 1，保持

## 任务

- [x] 0.1 NEW `docs/spec/changes/runtime-config-phase2/tasks.md`
- [x] 1.1 MODIFY `config.rs` 加 6 字段 + 6 个 parser helper + `eprintln` warnings
- [x] 1.2 MODIFY `config.rs` 加 `static RUNTIME_CONFIG: LazyLock<RuntimeConfig>` + `pub fn runtime_config()`
- [x] 1.3 MODIFY `gc/safepoint.rs` `throttle_n` —— OnceLock 删除，改 `runtime_config().safepoint_throttle`
- [x] 1.4 MODIFY `gc/arc_heap.rs` `minor_escalation_threshold` 改 `runtime_config().gc_minor_threshold`
- [x] 1.5 MODIFY `gc/types.rs` `pause_window_cap_from_env` 改 `runtime_config().gc_pause_window`
- [x] 1.6 MODIFY `gc/soft_registry.rs` `soft_threshold_from_env` 改 `runtime_config().gc_soft_threshold`
- [x] 1.7 MODIFY `gc/mode.rs` `GcMode::from_env` 改 `runtime_config().gc_mode`
- [x] 1.8 MODIFY `native/ext.rs` `native_search_paths` 改 `runtime_config().native_search_paths.extend()`
- [x] 1.9 MODIFY `config.rs` `tests` 加 8 个新 from_getter 测试（每个 parser + 默认值 + 不相关 env 忽略）
- [x] 1.10 UPDATE 2 个 obsolete subsystem env-var 测试 → 改为 delegator smoke-test（parsing 测试在 config 模块）
- [x] 1.11 VERIFY runtime `cargo test --lib` 767+21 全过 / compiler `dotnet test` 1453 全过
- [x] 1.12 MODIFY `review.md` 标 ✅ Phase 2 closed
- [x] 1.13 归档 + commit + push

## 备注

### 测试调整

2 个原有 subsystem 测试（`pause_window_cap_from_env_clamps_and_falls_back`,
`native_search_paths_respects_env_var`）依赖 `std::env::set_var` 反复改 env
然后 call subsystem 函数读最新值。Phase 2 后 subsystem 读 `LazyLock` 单
例，env 变化不反映 → 这两个测试**结构上不能成立**。改为 delegator
smoke-test（confirm 函数返回合理 sane 值），detailed parsing 测试由
`config::tests` via `from_getter(fake_env)` 覆盖。

### 仍保留的 subsystem 局部

- `Z42_STRESS_ITERS` / `Z42_STRESS_SEED` (`gc/arc_heap_tests/stress.rs`) ——
  test-only knob，故意不进 RuntimeConfig（production 路径不背 test-only
  字段）
- 现有的 `PAUSE_WINDOW_ENV_MUTEX` 之类的 test 互斥锁 —— 一些保留测试仍持
  锁但已无 set_var；harmless

### LazyLock 一次性 init 的取舍

`runtime_config()` first-call 解析所有 knob + 输出所有 warning。这意味着
本地反复 `cargo test` 启动多个进程时，每个进程独立 init 一次（与之前
OnceLock 行为一致）。CI 在干净 env 下一次 process 内永远是默认值。

