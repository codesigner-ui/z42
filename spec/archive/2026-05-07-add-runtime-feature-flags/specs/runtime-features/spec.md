# Spec: Runtime Feature Flags

## ADDED Requirements

### Requirement: Cargo features 定义

#### Scenario: features 段含完整 6 个 feature

- **WHEN** 阅读 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[features]` 段
- **THEN** 含字段：`default`、`jit`、`aot`、`interp-only`、`wasm`、`ios`、`android`

#### Scenario: 默认含 jit

- **WHEN** 检查 `default` 字段
- **THEN** 值为 `["jit"]`

#### Scenario: cranelift 改为 optional

- **WHEN** 检查 `[dependencies]`
- **THEN** cranelift / cranelift-jit / cranelift-module / cranelift-frontend / cranelift-codegen 全部带 `optional = true`

#### Scenario: 平台 preset 自动激活子 feature

- **WHEN** 检查 `wasm` feature 定义
- **THEN** 等于 `["interp-only"]`
- **AND** `ios` = `["interp-only", "aot"]`
- **AND** `android` = `["interp-only", "aot"]`

---

### Requirement: 源码 cfg gate

#### Scenario: lib.rs 模块声明 gate 正确

- **WHEN** 阅读 [src/runtime/src/lib.rs](src/runtime/src/lib.rs)
- **THEN** `mod jit` 前有 `#[cfg(feature = "jit")]`
- **AND** `mod aot` 前有 `#[cfg(feature = "aot")]`
- **AND** `mod interp` 无 cfg gate（永远编译）

#### Scenario: ExecutionMode enum gate

- **WHEN** 阅读 z42vm 的 Mode enum 定义
- **THEN** `Jit` variant 前有 `#[cfg(feature = "jit")]`
- **AND** `Aot` variant 前有 `#[cfg(feature = "aot")]`
- **AND** `Interp` variant 无 cfg

#### Scenario: 跨模块引用 jit 类型时加 cfg

- **WHEN** 任一 .rs 文件引用 `crate::jit::*`
- **THEN** 该引用点加 `#[cfg(feature = "jit")]` 或在已经 cfg-gated 的代码块内

---

### Requirement: 默认行为不变 (向后兼容硬指标)

#### Scenario: cargo build 行为不变

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml`（无任何 flag）
- **THEN** 编译产物含 cranelift JIT
- **AND** 二进制大小变化 < 1%

#### Scenario: cargo test 行为不变

- **WHEN** 执行 `cargo test --manifest-path src/runtime/Cargo.toml`
- **THEN** 全部测试通过；用例数与 P4.1 实施前一致

#### Scenario: z42vm --mode jit 仍可用

- **WHEN** 执行默认编译产物的 `z42vm <file>.zbc --mode jit`
- **THEN** JIT 路径正常执行；输出与 interp 模式一致

---

### Requirement: interp-only 编译

#### Scenario: 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features interp-only`
- **THEN** 编译成功，无错误

#### Scenario: 不含 cranelift

- **WHEN** 检查 `interp-only` 编译产物的依赖图（`cargo tree --no-default-features --features interp-only`）
- **THEN** 输出不含 cranelift / cranelift-jit / 等

#### Scenario: 二进制显著减小

- **WHEN** 对比 default vs interp-only 的 release 二进制大小
- **THEN** interp-only 减小 ≥ 30%

#### Scenario: z42vm interp-only 跑 vm_core 全绿

- **WHEN** 用 interp-only 编译产物跑 [src/runtime/tests/vm_core/](src/runtime/tests/vm_core/) 全部用例
- **THEN** 全绿（默认 mode=interp）

#### Scenario: z42vm interp-only 拒绝 --mode jit

- **WHEN** 执行 interp-only 产物的 `z42vm <file>.zbc --mode jit`
- **THEN** clap 报错 `"invalid value 'jit' for '--mode'"`，列出可选值仅含 `interp`

---

### Requirement: 平台 preset 编译验证（host target）

#### Scenario: wasm feature 编译通过

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features wasm`
- **THEN** 编译成功（host target，验证 feature 切分正确）

#### Scenario: ios feature 编译通过

- **WHEN** 执行 `cargo build --no-default-features --features ios`
- **THEN** 编译成功（host target）

#### Scenario: android feature 编译通过

- **WHEN** 执行 `cargo build --no-default-features --features android`
- **THEN** 编译成功（host target）

---

### Requirement: just / CI 接入

#### Scenario: just build-interp-only

- **WHEN** 执行 `just build-interp-only`
- **THEN** 触发 `cargo build --no-default-features --features interp-only`

#### Scenario: just 平台 build feature

- **WHEN** 执行 `just build-wasm-feature` / `build-ios-feature` / `build-android-feature`
- **THEN** 各自触发对应 feature 的 cargo build（host target）

#### Scenario: CI feature-matrix job

- **WHEN** PR 触发 CI
- **THEN** 含 `feature-matrix` job，依次跑 4 种 feature 组合编译
- **AND** 任一组合失败 → job 红

---

### Requirement: 文档同步

#### Scenario: cross-platform.md 完整描述 features

- **WHEN** 阅读 [docs/design/cross-platform.md](docs/design/cross-platform.md)
- **THEN** 含章节：feature 列表 / 平台 preset 矩阵 / cfg 编写规范 / 添加新 feature 指南

#### Scenario: README 含 features 说明

- **WHEN** 阅读 [src/runtime/README.md](src/runtime/README.md)
- **THEN** 含 "Features" 段，列出可用 feature 与默认值

#### Scenario: dev.md 加 Feature flags 段

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 含 "Feature Flags" 段，列出 just build-interp-only 等命令
