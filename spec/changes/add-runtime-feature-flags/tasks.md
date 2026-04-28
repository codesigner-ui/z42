# Tasks: Add Runtime Feature Flags

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P0 / P3 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: Cargo.toml 改造
- [ ] 阶段 2: 源码 cfg gate
- [ ] 阶段 3: z42vm CLI cfg
- [ ] 阶段 4: just / CI 接入
- [ ] 阶段 5: 文档同步
- [ ] 阶段 6: 验证

---

## 阶段 1: Cargo.toml 改造

- [ ] 1.1 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 新增 `[features]` 段，按 design.md Decision 1
- [ ] 1.2 改 cranelift 系列依赖为 `optional = true`
- [ ] 1.3 验证：`cargo metadata` 正确解析 features

## 阶段 2: 源码 cfg gate

- [ ] 2.1 [src/runtime/src/lib.rs](src/runtime/src/lib.rs) `mod jit` 加 `#[cfg(feature = "jit")]`
- [ ] 2.2 [src/runtime/src/lib.rs](src/runtime/src/lib.rs) `mod aot` 加 `#[cfg(feature = "aot")]`
- [ ] 2.3 grep 全部 `crate::jit::` 引用，加 cfg gate
- [ ] 2.4 grep 全部 `crate::aot::` 引用，加 cfg gate
- [ ] 2.5 `ExecutionMode` enum 的 `Jit` / `Aot` variant 加 cfg
- [ ] 2.6 验证：`cargo build`（默认）通过
- [ ] 2.7 验证：`cargo build --no-default-features --features interp-only` 通过

## 阶段 3: z42vm CLI cfg

- [ ] 3.1 [src/runtime/src/bin/z42vm.rs](src/runtime/src/bin/z42vm.rs) Mode enum variant 加 cfg
- [ ] 3.2 验证：默认编译 `z42vm --help` 显示 `--mode <interp|jit>`
- [ ] 3.3 验证：interp-only 编译 `z42vm --help` 显示 `--mode <interp>`
- [ ] 3.4 验证：interp-only 编译 `z42vm --mode jit` 报 clap 错误

## 阶段 4: just / CI 接入

- [ ] 4.1 [justfile](justfile) 加 `build-interp-only`
- [ ] 4.2 [justfile](justfile) 加 `build-wasm-feature` / `build-ios-feature` / `build-android-feature`
- [ ] 4.3 [.github/workflows/ci.yml](.github/workflows/ci.yml) 加 `feature-matrix` job
- [ ] 4.4 验证：CI feature-matrix job 4 个 build 全绿

## 阶段 5: 文档同步

- [ ] 5.1 [docs/design/cross-platform.md](docs/design/cross-platform.md) 新建（完整内容见 design.md Implementation Notes）
- [ ] 5.2 [src/runtime/README.md](src/runtime/README.md) 加 "Features" 段
- [ ] 5.3 [docs/dev.md](docs/dev.md) 加 "Feature Flags" 段
- [ ] 5.4 [docs/roadmap.md](docs/roadmap.md) 进度表加 P4.1 完成
- [ ] 5.5 [docs/design/vm-architecture.md](docs/design/vm-architecture.md) "构建 / feature flag" 章节同步

## 阶段 6: 验证

- [ ] 6.1 `cargo build` 默认通过
- [ ] 6.2 `cargo test` 默认全绿（用例数不变）
- [ ] 6.3 `cargo build --no-default-features --features interp-only` 通过
- [ ] 6.4 `cargo test --no-default-features --features interp-only` 全绿（jit-only 测试自动跳过）
- [ ] 6.5 `cargo build --no-default-features --features wasm` 通过
- [ ] 6.6 `cargo build --no-default-features --features ios` 通过
- [ ] 6.7 `cargo build --no-default-features --features android` 通过
- [ ] 6.8 `cargo tree --no-default-features --features interp-only` 不含 cranelift
- [ ] 6.9 二进制大小 interp-only vs default 减小 ≥ 30%（release）
- [ ] 6.10 默认编译产物 z42vm 跑 vm_core 全绿（interp + jit）
- [ ] 6.11 interp-only 产物 z42vm 跑 vm_core 全绿（仅 interp）
- [ ] 6.12 interp-only 产物 `z42vm --mode jit` 报 clap 错误
- [ ] 6.13 CI feature-matrix job 全绿

## 备注

### 实施依赖

- P0 已建好 [.github/workflows/ci.yml](.github/workflows/ci.yml)
- P3 已迁移好 [src/runtime/tests/vm_core/](src/runtime/tests/vm_core/)（用于 interp-only 验证）

### 风险

- **风险 1**：JIT 与 interp 之间的接口耦合可能比预期深 → 实施前先 grep `crate::jit::` 数量评估工作量
- **风险 2**：clap derive 对 cfg-gated enum variant 的支持是否完善 → 实测；fallback 是手写 `--mode` 解析
- **风险 3**：测试文件中可能有依赖 jit 的 hidden 引用 → 测试侧 cargo test --no-default-features 跑通才算完成
- **风险 4**：cranelift 是 transitive 依赖时 `optional = true` 可能不生效 → 如发生，用 `default-features = false` 强制断链

### 工作量估计

1–2 天（grep + cfg gate 是体力活；CI 验证 0.5 天）。
