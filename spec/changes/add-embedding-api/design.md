# Design: Embedding / Hosting API

> 详细设计见 [docs/design/embedding.md](../../../docs/design/embedding.md)。本文聚焦**实施级**决策（代码组织、并发、错误转换、与 native 模块的边界），不重复 user-facing API。

## 实施架构

```
┌────────────────────────────────────────────────────────────┐
│  Tier 1 C ABI — z42_host.h                                  │
│  (z42_host_initialize / load_zbc / resolve / invoke /       │
│   set_*_sink / last_error / shutdown)                       │
└──────────────┬─────────────────────────────────────────────┘
               │ extern "C"
               ▼
┌────────────────────────────────────────────────────────────┐
│  src/runtime/src/host/                                      │
│   mod.rs       — extern "C" 导出 + 函数路由                  │
│   config.rs    — Z42HostConfig 校验、sink 包装               │
│   state.rs     — OnceCell<Mutex<HostState>> 单实例           │
│   module.rs    — Slab<ModuleEntry> 句柄表（u32 idx + gen）  │
│   entry.rs     — FQN 解析（namespace.Type::method）          │
│   error.rs     — Z42HostStatus + thread-local last_error    │
└──────────────┬─────────────────────────────────────────────┘
               │ 复用
               ▼
┌────────────────────────────────────────────────────────────┐
│  src/runtime/src/{vm,interp,metadata,exception,native}      │
└────────────────────────────────────────────────────────────┘
```

## 关键决策

### D1. 单实例承载机制

```rust
// state.rs
static HOST: OnceCell<Mutex<HostState>> = OnceCell::new();

pub(crate) struct HostState {
    pub config:   ResolvedConfig,
    pub modules:  Slab<ModuleEntry>,   // ModuleRef = idx | (gen << 32)
    pub entries:  Slab<EntryEntry>,    // EntryRef  = idx | (gen << 32)
    pub stdout:   Option<SinkBox>,
    pub stderr:   Option<SinkBox>,
}
```

- `OnceCell::get()` == `Some` 表示已初始化；`initialize` 第二次调用返回 `ERR_ALREADY_INIT`
- `shutdown` 把 `HostState` 替换为 None（`OnceCell` 不支持 reset → 改用 `RwLock<Option<HostState>>` 包一层；详见 D5）
- 句柄用 generational index，`shutdown` 后旧句柄自动失效

### D2. 句柄表 vs 裸指针

候选：
- (a) 裸指针 `*mut HostStateModule`（CoreCLR 风格）
- (b) generational slab index 编进 u64（JNI 风格）

**选 (b)**：
- shutdown 后旧 `Z42ModuleRef` 调 invoke 不会 use-after-free → 直接命中 `ERR_NOT_INIT`
- 不需要在 ABI 上暴露 Rust struct 内部布局
- slab 引入 `slab` crate 即可（已是 z42-runtime 间接依赖）

### D3. 错误码翻译

```rust
// error.rs
thread_local! {
    static LAST_ERROR: RefCell<Option<Z42HostError>> = RefCell::new(None);
}

pub(crate) fn set_error(code: Z42HostStatus, msg: impl Into<String>) -> Z42HostStatus { ... }
pub(crate) fn clear_error() { ... }
```

- 每次成功调用清空 TLS
- z42 throw 跨出顶层 → `set_error(ERR_VM_EXCEPTION, format!("{ex_fqn}: {msg}"))`
- Rust panic（`std::panic::catch_unwind` 兜底每个 extern "C" 入口）→ `ERR_INTERNAL`

### D4. stdout / stderr sink 注入

VM 端现状：`src/runtime/src/native/io.rs`（待确认实际位置）持有 `Box<dyn Write>` 形式 stdout writer。

接入方式：
1. host 注册 sink 时，包装成实现 `Write` 的 adapter（每次 `write` → 调 C 回调）
2. 把 adapter 写入 VM 全局 `stdout` 句柄
3. shutdown 时复原（v0.1 不需要复原 —— 进程重启即可，但 H3 会加测试覆盖此场景）

**注意**：sink 回调可能从不同线程触发（如果 z42 起子线程）。v0.1 文档约定 sink 必须线程安全；不强制 `Send + Sync` 在 ABI 上（C 没有这个概念），文档说明即可。

### D5. shutdown 语义

`OnceCell` 不支持 reset。两个选项：
- (a) 改用 `RwLock<Option<HostState>>` —— shutdown = `*lock = None`，下次 initialize 重建
- (b) `HostState` 内部 `enum { Uninit, Active(...), ShutDown }` —— 状态机

**选 (a)**：简单。`RwLock` 读路径 invoke 用 `read()`，shutdown 用 `write()`，符合直觉。

```rust
static HOST: RwLock<Option<HostState>> = RwLock::new(None);
```

### D6. 与 native 模块的边界

`src/runtime/src/native/` 处理 "z42 调用 native" 与 "native 注册类型"。
`src/runtime/src/host/` 处理 "native 启动 VM"。

两者共享：
- `Z42Value` / `Z42Args` 类型定义（在 `native::marshal` 中已有）
- `Z42Error` 结构（在 `native::error` 中已有）

host 模块引用 `crate::native::marshal::Z42Value`，不重复定义。

### D7. C 头文件组织

```c
// z42_host.h
#include "z42_abi.h"   // Z42Value / Z42Args / Z42Error 来自这里
// 然后定义 Z42HostRef / Z42ModuleRef / Z42EntryRef / Z42HostConfig / Z42HostStatus / 函数声明
```

不重复定义 `Z42Value` 等。

## H1 完成判定（最小 GREEN）

- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 成功
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features interp-only` 成功
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features ios` 成功
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features android` 成功
- [ ] `host_tests::initialize_then_shutdown` 通过
- [ ] `host_tests::initialize_twice_returns_already_init` 通过
- [ ] `host_tests::shutdown_then_reinitialize` 通过
- [ ] `host_tests::null_config_returns_bad_config` 通过
- [ ] `host_tests::bad_abi_version_returns_bad_config` 通过
- [ ] `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（含 pre-existing）
- [ ] [src/toolchain/host/README.md](../../../src/toolchain/host/README.md) 反映 H1 状态

## H1 不做的（为 H2 留接口）

- 真正加载 `.zbc` —— `load_zbc` 返回 `ERR_INTERNAL`（"H2 未实现"消息）
- FQN 解析 —— `resolve_entry` 返回 `ERR_INTERNAL`
- 实际 invoke —— `invoke` 返回 `ERR_INTERNAL`
- stdout sink 真正接到 VM —— 仅存到 `HostState`，VM 侧接入留 H2
- Tier 2 Rust crate `z42-host` —— 留 H2

## 风险

- **R1**：`OnceCell` → `RwLock<Option<...>>` 切换在某些 Rust nightly 行为差异 → 用 `std::sync::RwLock`，stable
- **R2**：`catch_unwind` 在 FFI 边界依赖 `UnwindSafe` —— Rust panic 在 extern "C" 边界本身已是 UB；用 `catch_unwind` 兜住即可，必要时 `AssertUnwindSafe`
- **R3**：feature `ios` / `android` 不含 `jit`，但 host 路径不能依赖 jit；测试矩阵已覆盖
- **R4**：`Slab` 在 `z42-runtime` 现有依赖图中是否已存在 —— 若不存在则加入；不引入其他间接依赖
