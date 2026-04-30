# Tasks: Add z42-test-runner + Test Metadata

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P0 (add-just-and-ci) 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: z42.test 库扩展（attribute + assertion）
- [ ] 阶段 2: z42-test-runner crate 骨架
- [ ] 阶段 3: discover / runner / format 三个模块
- [ ] 阶段 4: scripts/test-changed.sh
- [ ] 阶段 5: just 入口接入
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 验证

---

## 阶段 1: z42.test 库扩展

- [ ] 1.1 [src/libraries/z42.test/src/Test.z42](src/libraries/z42.test/src/Test.z42) 定义 `Test` / `Skip(reason: string)` / `Ignore` attribute
- [ ] 1.2 [src/libraries/z42.test/src/Failure.z42](src/libraries/z42.test/src/Failure.z42) `TestFailure : Exception` 类
- [ ] 1.3 [src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42) 完整 assertion API（design.md Decision 3）
  - `eq<T>` / `notEq<T>` / `isTrue` / `isFalse` / `throws<E>` / `near` / `fail`
- [ ] 1.4 [src/libraries/z42.test/z42.test.toml](src/libraries/z42.test/z42.test.toml) 暴露 public API
- [ ] 1.5 [src/libraries/z42.test/README.md](src/libraries/z42.test/README.md) 更新文档
- [ ] 1.6 [src/libraries/z42.test/tests/.gitkeep](src/libraries/z42.test/tests/.gitkeep) 占位（实际用例 P3 补）

## 阶段 2: z42-test-runner crate 骨架

- [ ] 2.1 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[workspace] members` 加 `../toolchain/test-runner`
- [ ] 2.2 [src/toolchain/test-runner/Cargo.toml](src/toolchain/test-runner/Cargo.toml) crate manifest
  - 依赖：`clap` v4、`serde` / `serde_json`、`anyhow`、`z42-runtime`（path）、`regex`
- [ ] 2.3 [src/toolchain/test-runner/src/main.rs](src/toolchain/test-runner/src/main.rs) CLI 入口（clap derive）
- [ ] 2.4 [src/toolchain/test-runner/README.md](src/toolchain/test-runner/README.md) 工具文档
- [ ] 2.5 [src/toolchain/README.md](src/toolchain/README.md) 子目录列表加 test-runner
- [ ] 2.6 验证：`cargo build -p z42-test-runner` 通过

## 阶段 3: discover / runner / format 模块

- [ ] 3.1 [src/toolchain/test-runner/src/discover.rs](src/toolchain/test-runner/src/discover.rs)
  - `discover(paths: &[Path]) -> Vec<TestCase>`
  - 扫 .zbc 提 attribute；签名校验；返回测试函数列表
- [ ] 3.2 [src/toolchain/test-runner/src/result.rs](src/toolchain/test-runner/src/result.rs)
  - `TestStatus` enum（Passed / Failed / Skipped）
  - `TestResult` struct
  - `TestSuiteResult` struct（含 summary）
- [ ] 3.3 [src/toolchain/test-runner/src/runner.rs](src/toolchain/test-runner/src/runner.rs)
  - `run(cases: Vec<TestCase>, options: RunOptions) -> TestSuiteResult`
  - 加载 zpkg、逐个 call_function、catch 异常、测时、超时
- [ ] 3.4 [src/toolchain/test-runner/src/format/mod.rs](src/toolchain/test-runner/src/format/mod.rs) Formatter trait
- [ ] 3.5 [src/toolchain/test-runner/src/format/tap.rs](src/toolchain/test-runner/src/format/tap.rs) TAP 13 输出
- [ ] 3.6 [src/toolchain/test-runner/src/format/json.rs](src/toolchain/test-runner/src/format/json.rs) JSON 输出
- [ ] 3.7 [src/toolchain/test-runner/src/format/pretty.rs](src/toolchain/test-runner/src/format/pretty.rs) TTY 输出（用 `colored` crate）
- [ ] 3.8 [src/toolchain/test-runner/tests/integration_test.rs](src/toolchain/test-runner/tests/integration_test.rs)
  - 至少覆盖：discover / passed / failed / skipped / filter / 各 format

## 阶段 4: scripts/test-changed.sh

- [ ] 4.1 [scripts/test-changed.sh](scripts/test-changed.sh) shell 实现，按 design.md Decision 8 接口
- [ ] 4.2 反向依赖关系硬编码（6 个 stdlib 库）
- [ ] 4.3 输出 JSON 用 `jq` 处理（要求 jq 已安装；脚本顶部检查）
- [ ] 4.4 `--dry-run` / `--base` / `--head` 参数支持
- [ ] 4.5 chmod +x

## 阶段 5: just 入口接入

- [ ] 5.1 [justfile](justfile) `test-changed` 替换 P0 占位为完整实现
- [ ] 5.2 `test-stdlib` 替换 P0 占位（带可选 lib 参数）
- [ ] 5.3 `test-integration` 仍占位（P3 实施）

## 阶段 6: 文档同步

- [ ] 6.1 [docs/design/testing.md](docs/design/testing.md) 元数据 + front-matter + 归属规则 + TAP/JSON 格式
- [ ] 6.2 [docs/design/test-runner.md](docs/design/test-runner.md) 实现原理（架构图、模块职责、调用流程）
- [ ] 6.3 [docs/dev.md](docs/dev.md) 加 "z42-test-runner" 段
- [ ] 6.4 [docs/roadmap.md](docs/roadmap.md) Pipeline 进度表加 P2 完成
- [ ] 6.5 [docs/design/vm-architecture.md](docs/design/vm-architecture.md) 在 toolchain 章节加 test-runner 简介

## 阶段 7: 验证

- [ ] 7.1 `cargo build -p z42-test-runner` 通过
- [ ] 7.2 `cargo test -p z42-test-runner` 集成测试全绿
- [ ] 7.3 z42.test 库能编译为 .zpkg
- [ ] 7.4 手动：写一个最小 test_demo.z42（含 1 通过 + 1 失败 + 1 skip） → 编译 → `z42-test-runner test_demo.zbc --format tap` 输出符合规范
- [ ] 7.5 `--format json` 输出能被 jq 解析
- [ ] 7.6 `--format pretty` 在 TTY 上有颜色，pipe 时无颜色（自动检测）
- [ ] 7.7 `scripts/test-changed.sh` 用模拟 git diff 测各种路径前缀
- [ ] 7.8 `just test-changed` 在仅修改 docs 时不触发任何测试
- [ ] 7.9 `just test-stdlib z42.test` 跑通 z42.test 自身的占位测试

## 备注

### 实施依赖

- 必须先完成 [add-just-and-ci](spec/changes/add-just-and-ci/) (P0)
- 不依赖 P1（benchmark）

### 与 C1 (design-interop-interfaces) 的关系

- C1 已建立 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) workspace；本 spec 复用并扩展
- C1 引入的 z42-rs / z42-abi crate 与本 spec 无依赖关系

### 风险

- **风险 1**：z42 异常机制是否成熟到能被 host catch？需先验证 [src/runtime/src/](src/runtime/src/) 是否暴露 panic/exception API
- **风险 2**：`[Test]` attribute 在 IR 中是否已支持参数化（`[Skip(reason: "...")]`）？需先确认 attribute 元数据格式
- **风险 3**：cargo workspace 跨目录引用（`../toolchain/test-runner`）可能有路径解析问题，需实测
- **风险 4**：jq 未安装会导致 test-changed.sh 失败 → 脚本顶部 sanity check + 友好报错

### 工作量估计

2–3 天（runner 实现是大头）。
