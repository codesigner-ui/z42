# Spec: Test Infrastructure

> 本 spec 定义重构完成后**对外可观察的行为契约**。每个 Phase 完成时应能逐条验证对应场景。
> 本变更（redesign-test-infra）本身只交付文档，不实施场景；场景验证由各 Phase 子 spec 执行。

## ADDED Requirements

### Requirement: 统一任务入口

#### Scenario: 通过 just 触发常用任务

- **WHEN** 用户在仓库根目录执行 `just test`
- **THEN** 工具触发全量测试（编译器 + VM + stdlib + integration），最终 exit code 反映整体结果

#### Scenario: 列出可用任务

- **WHEN** 用户执行 `just --list`
- **THEN** 输出包含 `test`、`bench`、`build`、`ci`、`platform` 五大顶层 task 及其子命令

#### Scenario: 现有脚本仍可独立运行

- **WHEN** 用户执行 `./scripts/test-vm.sh interp`
- **THEN** 行为与 just 接入前完全一致（向后兼容）

---

### Requirement: 测试归属规则

#### Scenario: 编译器单元测试位置

- **WHEN** 一个测试只验证 lexer/parser/typecheck/IR/zbc 编码行为（不执行 .zbc）
- **THEN** 测试位于 [src/compiler/z42.Tests/](src/compiler/z42.Tests/)，使用 xUnit

#### Scenario: VM 子系统单元测试位置

- **WHEN** 一个测试只验证某个 VM crate（如 gc / decoder / interp / jit）的内部行为
- **THEN** 测试位于 `src/runtime/crates/<crate>/tests/`，使用 cargo test

#### Scenario: VM 语义端到端测试位置

- **WHEN** 一个 .z42 用例**不依赖任何 stdlib 库**（纯算术 / 控制流 / 类 / 异常）
- **THEN** 测试位于 `src/runtime/tests/vm_core/`，front-matter 标注 `// @test-tier: vm_core`

#### Scenario: stdlib 库本地测试位置

- **WHEN** 一个 .z42 用例依赖某个 stdlib 库（如 z42.io 的 Console）
- **THEN** 测试位于该库的 `src/libraries/<lib>/tests/` 目录，front-matter 标注 `// @test-tier: stdlib:<lib>`

#### Scenario: 跨多个 stdlib 的集成测试位置

- **WHEN** 一个 .z42 用例同时依赖多个 stdlib 库或跨 zpkg 模块
- **THEN** 测试位于 [tests/integration/](tests/integration/)，front-matter 标注 `// @test-tier: integration`

#### Scenario: 编译器测试在自举前不迁移

- **WHEN** 自举尚未完成
- **THEN** 编译器测试保持在 [src/compiler/z42.Tests/](src/compiler/z42.Tests/) 不动，不迁移到 z42-test-runner

---

### Requirement: 增量测试

#### Scenario: 仅修改某个 stdlib 库

- **WHEN** git diff 仅触及 `src/libraries/z42.io/**`
- **THEN** `just test changed` 只运行 z42.io 的本地测试 + 依赖 z42.io 的 stdlib 测试 + integration

#### Scenario: 修改 VM 核心代码

- **WHEN** git diff 触及 `src/runtime/src/**`
- **THEN** `just test changed` 运行全部 cargo test + vm_core，但不触发 stdlib 本地测试

#### Scenario: 仅修改文档

- **WHEN** git diff 仅触及 `docs/**` 或 `spec/**`
- **THEN** `just test changed` 不触发任何代码测试，仅做 lint

---

### Requirement: stdlib 本地测试 (z42-test-runner)

#### Scenario: 测试发现

- **WHEN** `z42-test-runner src/libraries/z42.core/tests/` 运行
- **THEN** 工具自动发现该目录下所有 .zbc 中带 `[Test]` attribute 的函数

#### Scenario: 测试执行与结果输出

- **WHEN** 测试运行结束
- **THEN** 输出 TAP 或 JSON 格式结果，包含每个测试的：名称、通过/失败、耗时、失败原因（若有）

#### Scenario: 失败时的 exit code

- **WHEN** 任一测试失败
- **THEN** runner 以非零 exit code 退出，CI 任务标记为失败

---

### Requirement: Benchmark 与基线

#### Scenario: 运行 VM 微基准

- **WHEN** 用户执行 `just bench vm`
- **THEN** criterion 输出每个 benchmark 的中位数 / 置信区间，并将结果写入 `bench/baselines/<branch>.json`

#### Scenario: 与 main 基线对比

- **WHEN** 用户执行 `just bench --baseline main`
- **THEN** 工具输出当前分支与 main 基线的差异；任一指标退化超过 5% → exit 非零

#### Scenario: PR 性能门禁

- **WHEN** PR 触发 CI
- **THEN** CI 自动跑 quick benchmark 子集并与 main 基线对比；超过阈值则 PR 检查失败

---

### Requirement: CI 矩阵

#### Scenario: PR 触发增量测试

- **WHEN** 在 GitHub 上提交 pull request
- **THEN** CI 在 linux-x64 / macos-aarch64 / windows-x64 三个 runner 上各跑一次 `just test changed`

#### Scenario: 推送到 main 触发全量

- **WHEN** 推送或合并到 main
- **THEN** CI 跑 `just ci`（全量测试 + 全量 benchmark），并更新 main 基线

---

### Requirement: 跨平台 feature gate

#### Scenario: 默认配置含 JIT

- **WHEN** 执行 `cargo build --manifest-path src/runtime/Cargo.toml`
- **THEN** 编译产物含 Cranelift JIT，与重构前行为一致

#### Scenario: interp-only 配置不含 JIT

- **WHEN** 执行 `cargo build --no-default-features --features interp-only`
- **THEN** 编译产物不含 cranelift / cranelift-jit 依赖；二进制大小显著减小

#### Scenario: wasm 目标编译

- **WHEN** 执行 `cargo build --target wasm32-unknown-unknown --features wasm --no-default-features`
- **THEN** 编译通过，产物为 .wasm 文件

#### Scenario: iOS 目标编译

- **WHEN** 执行 `cargo build --target aarch64-apple-ios --features ios --no-default-features`
- **THEN** 编译通过，不含 JIT，含 AOT 入口

#### Scenario: Android 目标编译

- **WHEN** 执行 `cargo ndk -t arm64-v8a build --features android --no-default-features`
- **THEN** 编译通过，产物为 .so，初版默认 interp-only

---

### Requirement: 平台工程脚手架

#### Scenario: WebAssembly demo 跑通

- **WHEN** 用户执行 `just platform wasm test`
- **THEN** 在浏览器或 Node.js 中加载 wasm 模块，运行 `examples/01_hello.zbc` 输出 "Hello, World!"

#### Scenario: iOS demo 在 simulator 跑通

- **WHEN** 用户执行 `just platform ios test`
- **THEN** XCTest 在 iOS simulator 中加载 z42 framework，跑通至少一个 vm_core 用例

#### Scenario: Android demo 在 emulator 跑通

- **WHEN** 用户执行 `just platform android test`
- **THEN** JUnit 在 Android emulator 中通过 JNI 加载 z42 .so，跑通至少一个 vm_core 用例

#### Scenario: 跨平台一致性

- **WHEN** 同一份 vm_core .zbc 在 desktop / wasm / iOS / Android 上分别运行
- **THEN** 所有平台输出完全一致（字节级对比）
