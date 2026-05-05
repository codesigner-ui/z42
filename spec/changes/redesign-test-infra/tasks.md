# Tasks: Redesign Test Infrastructure

> 状态：🟡 进行中（仅规划阶段） | 创建：2026-04-29
> 本变更只交付 4 份规范文档；实际实施由 5 个独立 Phase 子 spec 执行。

## 进度概览

- [ ] 阶段 A: 本变更规范文档
- [ ] 阶段 B: P0 子 spec（基础设施）—— 后续独立立项
- [ ] 阶段 C: P1 子 spec（benchmark）—— 后续独立立项
- [ ] 阶段 D: P2 子 spec（z42-test-runner）—— 后续独立立项
- [ ] 阶段 E: P3 子 spec（用例迁移）—— 后续独立立项
- [ ] 阶段 F: P4 子 spec（跨平台）—— 后续独立立项

---

## 阶段 A: 本变更规范文档

- [x] A.1 创建 [spec/changes/redesign-test-infra/](spec/changes/redesign-test-infra/) 目录
- [x] A.2 写 [proposal.md](spec/changes/redesign-test-infra/proposal.md)
- [x] A.3 写 [design.md](spec/changes/redesign-test-infra/design.md)
- [x] A.4 写 [specs/test-infrastructure/spec.md](spec/changes/redesign-test-infra/specs/test-infrastructure/spec.md)
- [x] A.5 写本 tasks.md
- [ ] A.6 commit + push（含 `.claude/`、`spec/`）

---

## 阶段 B: Phase 0 — 基础设施先行（独立 spec）

> **不要在 P0 完成前做任何用例拆分或迁移**。
> 拟立 spec 名：`add-just-and-ci`

### 范围
- 新增 [justfile](justfile) 顶层 + 7 个现有脚本接入
- 约定 just task 命名空间（test / bench / build / ci / platform）
- 新增 [.github/workflows/ci.yml](.github/workflows/ci.yml) 三平台矩阵骨架
- CI 暂只跑 `just test`（等价于现有全量测试）+ `cargo build` + `dotnet build`
- README 加"开发者快速入门"段，引导使用 `just <task>`

### 验收（来自 spec 场景）
- ✅ `just test` 全量等价于 `dotnet test && ./scripts/test-vm.sh && ./scripts/test-cross-zpkg.sh`
- ✅ `just --list` 列出 5 大顶层 task
- ✅ 7 个现有脚本仍可独立运行
- ✅ GitHub Actions PR 跑通 linux-x64 / macos-aarch64 / windows-x64

### 不做
- 不动现有测试目录结构
- 不引入 z42-test-runner
- 不引入 benchmark

---

## 阶段 C: Phase 1 — Benchmark 框架（独立 spec）

> 依赖 P0 完成。拟立 spec 名：`add-benchmark-framework`

### 范围
- `src/runtime/benches/` + criterion 集成（解释器 / JIT 各 1 套基准）
- `src/compiler/z42.Bench/` + BenchmarkDotNet（编译吞吐基准）
- `bench/scenarios/*.z42` + 端到端 harness
- `bench/baselines/<branch>.json` 快照机制
- `just bench` / `just bench vm` / `just bench --baseline main`
- CI PR 阶段加 `just bench --quick` 性能门禁（>5% 退化阻塞）

### 验收
- ✅ `just bench vm` 输出 criterion 结果
- ✅ `just bench --baseline main` 能 diff 当前与 main
- ✅ PR 引入 >5% 退化时 CI 失败

---

## 阶段 D: Phase 2 — z42-test-runner + 元数据规范（独立 spec）

> 依赖 P0 完成。拟立 spec 名：`add-z42-test-runner`

### 范围
- 新建 `src/toolchain/test-runner/`（占位目录已存在）
  - 实现：测试发现（`[Test]` attribute）+ 加载 .zbc + 调用 z42.test assertion + TAP/JSON 输出
- 元数据标签规范：[docs/design/testing.md](docs/design/testing.md)（新文档）
  - `// @test-tier: vm_core | stdlib:<lib> | integration` front-matter 约定
- 新增 [scripts/test-changed.sh](scripts/test-changed.sh) + 接入 `just test changed`
- z42.test 库可能需要补 assertion API（`assertEq`, `assertThrows`, `assertNear` 等）

### 验收
- ✅ `z42-test-runner <dir>` 能发现 `[Test]` 函数并运行
- ✅ 失败时非零 exit
- ✅ `just test changed` 基于 git diff 输出受影响测试集

### 不做
- **不迁移任何现有 golden**（保留在 src/runtime/tests/golden/run/）
- **不动编译器测试**（C# xUnit 不变）

---

## 阶段 E: Phase 3 — 用例迁移（独立 spec）

> 依赖 P2 完成。拟立 spec 名：`migrate-tests-by-ownership`
>
> **2026-05-05 状态更新**：由 [`spec/changes/migrate-runtime-tests-by-ownership/`](../migrate-runtime-tests-by-ownership/) 落地（按 dotnet/runtime 风格分类，非原计划"VM-only 留 runtime/tests/golden/run/"模式）。变化见该 spec 的 tasks.md。

### 范围
- 给现有 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/) 103 个用例每个加 `@test-tier:` 注释
- 按 tag 分流：
  - `vm_core` → 移到 `src/runtime/tests/vm_core/`
  - `stdlib:<lib>` → 移到 `src/libraries/<lib>/tests/`
  - `integration` → 移到 [tests/integration/](tests/integration/)
- stdlib 各库（z42.core / z42.collections / z42.math / z42.io / z42.text）在迁移基础上**至少补 1 个本地原生测试**
- 老的 `src/runtime/tests/golden/run/` 目录在迁移完成后删除
- 更新 [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh) 适配新路径

### 验收
- ✅ `src/runtime/tests/golden/run/` 不再存在
- ✅ 全部 103+ 用例分流到对应目录，全绿
- ✅ 每个 stdlib 库 `tests/` 至少 1 个原生测试通过
- ✅ `just test stdlib core` 仅运行 z42.core 测试

### 编译器测试处理
- **编译器 golden 测试 [src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs) 不动**
- 自举完成后再立独立 spec 评估是否合并

---

## 阶段 F: Phase 4 — 跨平台脚手架（独立 spec，最后做）

> 依赖 P3 完成。拟分 4 个子 spec 串行：

### F.1 `add-runtime-feature-flags`（前置）
- [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 加 `[features]`：jit / aot / interp-only / wasm / ios / android
- `mod jit` / `mod aot` 加 `#[cfg(feature = "...")]`
- 验收：`cargo build --no-default-features --features interp-only` 能编译通过；默认行为不变

### F.2 `add-platform-wasm`
- 新建 [platform/wasm/](platform/wasm/)：wasm-bindgen + JS API + index.html demo
- `just platform wasm build/test`
- playwright e2e 跑 `examples/01_hello.zbc`
- 验收：浏览器输出 "Hello, World!"

### F.3 `add-platform-android`
- 新建 [platform/android/](platform/android/)：Gradle AAR + JNI 入口 + AndroidDemo + JUnit
- `just platform android build/test`
- 默认 interp-only（JIT 后期评估）
- 验收：emulator 中至少 1 个 vm_core 用例通过

### F.4 `add-platform-ios`
- 新建 [platform/ios/](platform/ios/)：SwiftPM Package + bridging header + iOSDemo + XCTest
- `just platform ios build/test`
- interp-only + AOT 占位
- 验收：iOS simulator 中至少 1 个 vm_core 用例通过

### F.5（后置评估，不在本次规划范围）
- Android JIT 实测：Pixel + 三星 + 小米三档机型，验证 Cranelift aarch64 + W^X
- 通过则启用 `android = ["jit", ...]` 默认值

---

## 备注

### 优先级原则（User 钉死）

1. **基础设施先行**：P0 / P1 / P2 完成前**绝对不做用例拆分迁移**（P3）
2. **编译器测试自举前不迁移**：[src/compiler/z42.Tests/](src/compiler/z42.Tests/) 保持 C# xUnit 现状
3. **跨平台最后做**：P4 在 P0–P3 全绿之后启动

### 文档同步要求

每个 Phase 归档时必须同步：

| Phase | 同步文档 |
|-------|---------|
| P0 | [docs/dev.md](docs/dev.md)（just 入口）+ [.claude/CLAUDE.md](.claude/CLAUDE.md)（构建段） |
| P1 | [docs/design/benchmark.md](docs/design/benchmark.md)（新建） |
| P2 | [docs/design/testing.md](docs/design/testing.md)（新建） |
| P3 | [docs/design/testing.md](docs/design/testing.md)（迁移规则）+ [.claude/rules/code-organization.md](.claude/rules/code-organization.md)（测试目录约定） |
| P4 | [docs/design/cross-platform.md](docs/design/cross-platform.md)（新建）+ [docs/dev.md](docs/dev.md)（平台命令） |

### 工作量估计

| Phase | 估时 |
|-------|------|
| 本变更（A） | 已完成，仅文档 |
| P0 | 0.5–1 天 |
| P1 | 1–2 天 |
| P2 | 2–3 天（含 z42-test-runner 实现） |
| P3 | 2–3 天（103 用例迁移 + stdlib 本地测试） |
| P4 | 5–8 天（4 个子 spec 串行，含跨平台调试） |

总计约 11–17 个工作日，分布在多个 sprint。
