# Design: Redesign Test Infrastructure

## Architecture

```
                ┌─────────────────────────────────────────────────────────┐
                │                  just (任务编排器)                      │
                │  test / bench / build / platform / ci  统一入口         │
                └────────┬─────────────────────┬──────────────────────────┘
                         │                     │
            ┌────────────┴────────┐  ┌─────────┴──────────────┐
            │  Test Pipelines     │  │  Benchmark Pipelines   │
            │                     │  │                        │
            │  ├─ compiler        │  │  ├─ compiler (BDN)     │
            │  ├─ vm_core         │  │  ├─ vm (criterion)     │
            │  ├─ stdlib/<lib>    │  │  └─ end-to-end (.z42)  │
            │  └─ integration     │  │     baselines/*.json   │
            └────────┬────────────┘  └────────┬───────────────┘
                     │                        │
            ┌────────┴────────┐      ┌────────┴────────┐
            │ z42-test-runner │      │  GitHub Actions │
            │ (.z42 测试驱动)  │      │  (CI 矩阵)       │
            └─────────────────┘      └─────────────────┘

Cross-Platform: platform/{wasm,android,ios}/ 各自工程 + feature gate
```

## Decisions

### Decision 1: 测试归属规则 ——「被测对象在哪，测试就在哪」

**问题**：当前 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/) 103 个用例混测语法/类型/VM/stdlib。

**选项**：
- A. 按 pipeline 阶段分（lexer/parser/typecheck/codegen/vm）—— 与现有 C# xUnit 重叠
- B. 按被测对象分（compiler/vm_core/stdlib/<lib>/integration）—— 与代码组织对齐
- C. 不动现状，只加 tag

**决定**：选 **B**。归属表：

| 测试目录 | 职责 | 输入 | 框架 |
|---------|------|------|------|
| [src/compiler/z42.Tests/](src/compiler/z42.Tests/) | 编译器单元（lexer/parser/typecheck/IR/zbc） | C# 单测 | xUnit（保留） |
| `src/compiler/z42.Tests/golden_compile/` | 编译器端到端：`.z42 → .zbc` 二进制契约 | .z42 + 期望 .zbc | xUnit + golden |
| `src/runtime/crates/<sub>/tests/` | VM 子系统单元（gc/decoder/interp/jit 各自） | Rust 单测 | cargo test |
| `src/runtime/tests/vm_core/` | VM 语义端到端（**不依赖 stdlib** 的最小 .zbc） | 算术/控制流/类/异常 | cargo test |
| `src/libraries/<lib>/tests/*.z42` | 各 stdlib 库本地功能测试 | .z42 测试源 | **z42-test-runner** |
| `tests/integration/` | 跨模块/跨 zpkg/跨语言端到端 | shell + golden | 保留现 `test-cross-zpkg.sh` |

**编译器测试保持 C# xUnit 现状不迁移**（自举完成前）。

### Decision 2: 现有 golden 迁移规则 ——「dependency tag」

每个 .z42 用例顶部加 front-matter 注释，按"最深依赖"归位：

```z42
// @test-tier: vm_core         // 不依赖 stdlib
// @test-tier: stdlib:z42.io   // 依赖 io.Console.println → 进 z42.io/tests
// @test-tier: integration     // 跨多个 stdlib
```

迁移示例：
- [01_hello/source.z42](src/runtime/tests/golden/run/01_hello/source.z42) 用 `Console.println` → `src/libraries/z42.io/tests/`
- 纯算术用例 → `src/runtime/tests/vm_core/`
- 跨多 stdlib 用例 → `tests/integration/`

### Decision 3: stdlib 测试 runner ——「自建 z42-test-runner」

**问题**：6 个 stdlib 库无本地测试，怎么跑 `.z42` 测试源？

**选项**：
- A. 沿用 golden 模式（.z42 + expected_output.txt）—— 简单但无 assertion 表达力
- B. 自建 z42-test-runner，复用已存在的 `z42.test` 库 —— 长期受益，但要写新工具

**决定**：选 **B**。

实现要点：
- 每个库 `tests/*.z42` 编译为独立 .zbc，由 `z42-test-runner` 加载执行
- 测试发现：基于 `[Test]` attribute（IR 已支持 attribute 元数据）
- 失败收集：输出 TAP 或 JSON 格式（CI 友好）
- 工具规模：~300 行，落到 `src/toolchain/test-runner/`（已有占位）

### Decision 4: 任务编排器 ——「just」

**问题**：7 个 shell 脚本无统一入口。

**选项**：
- A. `just`（轻量、零编译、跨平台）
- B. cargo `xtask`（每次编译 Rust，启动慢）
- C. 沿用 shell 脚本 + Makefile 包装

**决定**：选 **A**。理由：~2MB 静态二进制，启动 ms 级，CI 友好；保留现有 7 个 shell 脚本作为 just task 的实现。

入口约定：

```
just test                # 全量
just test compiler       # 只编译器
just test stdlib         # 全部 stdlib
just test stdlib core    # 只 z42.core
just test vm             # 只 vm_core + crates 单测
just test changed        # 基于 git diff 的增量
just bench               # 全部 benchmark
just bench vm            # 只 VM benchmark
just ci                  # CI 标准管线
just platform wasm test  # 跨平台测试
```

### Decision 5: 增量测试 ——「test-changed.sh」

新增 [scripts/test-changed.sh](scripts/test-changed.sh)，输入 git diff 范围，按文件路径前缀映射受影响子集：

| 改动路径前缀 | 触发的测试集 |
|-------------|------------|
| `src/compiler/**` | `dotnet test` + 全部 golden（编译器是基座） |
| `src/runtime/crates/<crate>/**` | `cargo test -p <crate>` + `vm_core` |
| `src/runtime/src/**` | 全部 cargo test + `vm_core` |
| `src/libraries/<lib>/**` | 该库 `tests/` + 依赖它的 stdlib + integration |
| `docs/**` 或 `spec/**` | 不触发（仅 lint） |

### Decision 6: Benchmark 框架

| 维度 | 工具 | 位置 | 度量 |
|------|------|------|------|
| Rust 微基准 | `criterion` | `src/runtime/benches/` | ns/op，置信区间 |
| 编译器吞吐 | `BenchmarkDotNet` | `src/compiler/z42.Bench/` | 编译速度（行/秒） |
| .z42 端到端 | 自建 harness | `bench/scenarios/*.z42` | 启动时间 + 总耗时 |
| 基线对比 | JSON 快照 | `bench/baselines/<branch>.json` | 回归阈值 >5% 警报 |

`just bench --baseline main` 自动 diff 当前分支 vs main，PR 检查不通过则阻塞。

### Decision 7: CI ——「GitHub Actions + 矩阵」

新增 [.github/workflows/ci.yml](.github/workflows/ci.yml)：

- `pull_request`：`just test changed` + `just bench --quick`
- `push to main`：`just ci`（全量）+ 更新基线
- 矩阵：`linux-x64` / `macos-aarch64` / `windows-x64`
- 缓存：`actions/cache` 缓存 cargo target、`~/.nuget`、`bench/baselines/`

### Decision 8: 跨平台矩阵与 feature flags

平台-执行模式矩阵（已修正 Cranelift 在 Android 上技术可行）：

| 平台 | Target Triple | interp | JIT | AOT | 默认配置 |
|------|--------------|:------:|:---:|:---:|---------|
| Desktop x64 | `x86_64-unknown-linux-gnu` 等 | ✅ | ✅ Cranelift | ✅ | `default = ["jit"]` |
| Desktop arm64 | `aarch64-apple-darwin` | ✅ | ✅ Cranelift | ✅ | `default = ["jit"]` |
| **WebAssembly** | `wasm32-unknown-unknown` | ✅ | ❌（wasm 沙箱禁动态代码生成）| ⚠️ wasm 自身即编译产物 | `wasm = ["interp-only"]` |
| **iOS** | `aarch64-apple-ios` | ✅ | ❌（App Store 政策硬禁）| ✅ AOT 主路径 | `ios = ["interp-only", "aot"]` |
| **Android** | `aarch64-linux-android` | ✅ | ✅ 技术可行（实测后启用）| ✅ | `android = ["interp-only", "aot"]` 起步，JIT 后期评估 |

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) 改造：

```toml
[features]
default = ["jit"]
jit = ["dep:cranelift", "dep:cranelift-jit"]
aot = []
interp-only = []
wasm = ["interp-only"]
ios = ["interp-only", "aot"]
android = ["interp-only", "aot"]
```

代码侧：`#[cfg(feature = "jit")] mod jit;`

### Decision 9: 平台工程脚手架

```
platform/
├── wasm/           # wasm-bindgen + JS API + index.html demo + playwright e2e
├── ios/            # SwiftPM Package + bridging header + iOSDemo.xcodeproj + XCTest
└── android/        # Gradle AAR + JNI 入口 + AndroidDemo + JUnit
```

每个平台目录提供：
- 构建脚本（`just platform <name> build`）
- 测试脚本（`just platform <name> test`）—— wasm: wasm-bindgen-test；iOS: simulator XCTest；Android: emulator
- 一个最小 demo 工程，加载 `examples/01_hello.zbc` 跑通

跨平台一致性测试集：复用 `vm_core/` 的 .zbc 用例（不依赖 JIT），同一份 .zbc 在所有平台跑出相同输出。

## Phase Plan（按优先级排序）

> **优先级原则（User 钉死）**：
> 1. test/benchmark **机制**先行，机制就绪前不做用例拆分迁移
> 2. **编译器自举完成前**，编译器测试保留 C# xUnit 不迁移
> 3. 跨平台**最后**做（feature flags 是前置）

| Phase | 名称 | 范围 | 依赖 | 风险 |
|-------|------|------|------|------|
| **P0** | `add-just-and-ci` | just runner + 7 脚本接入 + GitHub Actions CI 骨架 | 无 | 低 |
| **P1** | `add-benchmark-framework` | criterion + BenchmarkDotNet + baseline 机制 + `just bench` | P0 | 低 |
| **P2** | `add-z42-test-runner` | z42-test-runner 工具 + 元数据标签规范 + test-changed.sh | P0 | 中（新工具） |
| **P3** | `migrate-tests-by-ownership` | 现有 golden 按 dependency tag 分流到 vm_core / stdlib/<lib> / integration；stdlib 各库补本地测试 | P2 | 中（103 用例迁移） |
| **P4** | `add-cross-platform-scaffolds` | feature flags 拆分 → wasm → android → ios（依次） | P3 | 高（首次跨平台） |

每个 Phase 后续作为独立 `spec/changes/<name>/` 走完整 spec-first 流程。本变更只交付规划。

**编译器测试不在迁移范围**：保留 [src/compiler/z42.Tests/](src/compiler/z42.Tests/) C# xUnit 现状；自举完成后再立专项 spec 评估是否统一到 z42-test-runner。

## Implementation Notes

### 现有 7 个脚本如何融入 just

P0 阶段不删除任何现有脚本，just task 内部调用：

```just
test-vm:
    ./scripts/test-vm.sh

test-cross-zpkg:
    ./scripts/test-cross-zpkg.sh

test:
    @just test-compiler
    @just test-vm
    @just test-cross-zpkg
```

后续 Phase 视情况把脚本逻辑内联到 justfile 或保留外置。

### z42-test-runner 与 z42.test 的关系

[src/libraries/z42.test/](src/libraries/z42.test/) 已存在但未启用，提供 assertion API（`assertEq`, `assertThrows` 等）。z42-test-runner 是宿主端工具：
- 加载库的 `tests/*.zbc`
- 通过 `[Test]` attribute 发现测试函数
- 调用 z42.test 库的 assertion → 收集结果 → TAP/JSON 输出

两者职责切分：**z42.test 是库代码（assertion）**，**z42-test-runner 是宿主工具（发现 + 调度）**。

### 跨平台 Cranelift JIT 处理

P4.1（feature flags 拆分）必须在 P4.2/P4.3/P4.4 之前完成。具体顺序：

1. **P4.1**：`src/runtime/Cargo.toml` 加 features，`mod jit` 加 `#[cfg(feature = "jit")]`，确保 `--no-default-features --features interp-only` 能编译通过
2. **P4.2 wasm**：[platform/wasm/](platform/wasm/) 工程 + wasm-bindgen 桥接 + 最小 demo
3. **P4.3 android**：[platform/android/](platform/android/) Gradle AAR + JNI + 默认 interp-only
4. **P4.4 ios**：[platform/ios/](platform/ios/) SwiftPM + interp-only + AOT 占位

Android JIT 的实测放到 P4 完成之后单独评估（实测 Pixel + 三星 + 小米三档机型）。

## Testing Strategy

本变更只交付规范文档，**无代码变更**。验证方式：

- 文档可读性：4 份文档结构完整、决策清晰、引用路径正确
- 规则一致性：与 [.claude/rules/workflow.md](.claude/rules/workflow.md) 不冲突
- Phase 依赖正确：P0→P1→P2→P3→P4 顺序无环、依赖明确
- Scope 表与 tasks.md 双向对齐

每个 Phase 后续 spec 自带其测试策略（criterion bench 验证、CI 矩阵验证、跨平台 demo 验证等）。
