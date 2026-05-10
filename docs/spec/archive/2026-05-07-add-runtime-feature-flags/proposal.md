# Proposal: Add Runtime Feature Flags (jit / aot / interp-only / wasm / ios / android)

## Why

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) 当前**无 [features] 段**，导致：

1. **JIT 与平台耦合**：Cranelift 是 x64/aarch64 桌面专属，wasm 沙箱禁动态代码生成、iOS App Store 政策硬禁 JIT；当前无法编译"不含 JIT"的版本
2. **二进制大小臃肿**：移动端 / wasm 不需要 JIT，但当前 cranelift 链接进所有产物
3. **跨平台 P4.2/P4.3/P4.4 无法启动**：必须先有 feature 切分才能为 wasm/iOS/Android 产出对应二进制

P4.1 是**纯前置工作**，不交付任何平台脚手架，只把 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 切分成可组合的 feature 集合，并在源码侧加 `#[cfg(feature = "...")]` gate。验证标准：`--no-default-features --features interp-only` 能编译通过且行为与去掉 JIT 模块后一致；默认 `cargo build` 行为完全不变（向后兼容）。

## What Changes

- **[src/runtime/Cargo.toml](src/runtime/Cargo.toml) 加 `[features]` 段**：
  - `default = ["jit"]`（向后兼容）
  - `jit`、`aot`、`interp-only`、`wasm`、`ios`、`android` 6 个 feature
- **依赖切分**：cranelift 系列改为 optional dependency，仅 `jit` feature 启用
- **源码 cfg gate**：
  - `mod jit` 加 `#[cfg(feature = "jit")]`
  - `mod aot` 加 `#[cfg(feature = "aot")]`
  - 涉及 JIT 的公共 API（如 `Interpreter::with_jit()`）加 cfg
- **z42vm 二进制 cfg**：CLI `--mode jit` 在 `interp-only` 配置下编译时移除该选项；运行时若用户传 `jit` 报错 `"jit feature not compiled"`
- **just 接入**：新增 `just build interp-only` / `just build wasm` 等子命令（实际平台构建在 P4.2-P4.4 完成）
- **CI 验证**：CI 加一个 job 跑 `cargo build --no-default-features --features interp-only`，确保拆分不漏 cfg
- **文档**：新建 [docs/design/cross-platform.md](docs/design/cross-platform.md)

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml` | MODIFY | `[dependencies]` 改 cranelift 为 optional；新增 `[features]` 段 |
| `src/runtime/src/lib.rs` | MODIFY | `mod jit` / `mod aot` 加 cfg；re-export 调整 |
| `src/runtime/src/jit/mod.rs` | MODIFY | 顶部加 `#![cfg(feature = "jit")]` 或不变（仅 mod 声明被 cfg gate） |
| `src/runtime/src/aot/mod.rs` | MODIFY | 同上 |
| `src/runtime/src/interp/mod.rs` | MODIFY | 移除对 jit 模块的非 cfg 引用（若有） |
| `src/runtime/src/bin/z42vm.rs` | MODIFY | CLI `--mode` 选项加 cfg；运行时检查 |
| `src/runtime/src/lib_tests.rs` 或类似 | MODIFY | 若有依赖 JIT 的测试，加 `#[cfg(feature = "jit")]` |
| `src/runtime/Cargo.lock` | MODIFY | 自动重生 |
| `justfile` | MODIFY | 加 `build-interp-only` 等 task |
| `.github/workflows/ci.yml` | MODIFY | 加 interp-only 编译验证 job |
| `docs/design/cross-platform.md` | NEW | 平台-feature 矩阵 + cfg 规范 |
| `docs/dev.md` | MODIFY | 加 "Feature flags" 段 |
| `src/runtime/README.md` | MODIFY | 加 features 段说明 |

**只读引用**：
- [src/runtime/src/jit/](src/runtime/src/jit/) — 理解 JIT 模块边界
- [src/runtime/src/aot/](src/runtime/src/aot/) — 理解 AOT 模块边界
- [src/runtime/src/interp/](src/runtime/src/interp/) — 理解 interp 模块边界
- [src/runtime/Cargo.toml](src/runtime/Cargo.toml) — 现有依赖
- [.github/workflows/ci.yml](.github/workflows/ci.yml) — P0 已建好

## Out of Scope

- **任何平台工程目录**（`platform/wasm/` / `platform/ios/` / `platform/android/`）：P4.2 / P4.3 / P4.4 范围
- **wasm/ios/android target 实际编译验证**：本 spec 只验证 feature gate 正确切分；为 wasm-target 编译留给 P4.2
- **AOT 实际实现**：当前 [src/runtime/src/aot/](src/runtime/src/aot/) 是占位；本 spec 不实现 AOT，只 gate 占位代码
- **mutually-exclusive feature 检查**：Cargo features 是 additive 的；本 spec 不加 `compile_error!` 互斥检查（feature combo 由顶层 platform spec 控制）
- **JIT 模块的内部重构**：仅加 cfg，不动逻辑

## Open Questions

- [ ] **Q1**：`interp-only` 是否仅作为"baseline tag"（空 feature），还是带语义？
  - 倾向：作为 tag（空 feature，主要用于命名意图清晰）
- [ ] **Q2**：`wasm` feature 是否自动激活 `interp-only`？
  - 倾向：是（feature dependency: `wasm = ["interp-only"]`）
- [ ] **Q3**：`ios` 是否同时激活 `interp-only` 和 `aot`？
  - 倾向：是；Android 同
- [ ] **Q4**：默认 `cargo build` 是否保留 JIT？
  - 倾向：**保留**（`default = ["jit"]`，向后兼容现有 dev workflow）
- [ ] **Q5**：JIT 缺失时 z42vm CLI 如何报错？
  - 倾向：编译期 `--mode` 选项不暴露 jit 选项；运行时若收到 `--mode=jit` 报 "jit not compiled"
