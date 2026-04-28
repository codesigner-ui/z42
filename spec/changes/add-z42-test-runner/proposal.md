# Proposal: Add z42-test-runner + Test Metadata Spec

## Why

z42 标准库（z42.core / z42.collections / z42.math / z42.io / z42.text / z42.test）**完全没有本地测试机制**。当前所有 stdlib 验证依赖 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/) 的 103 个 golden 用例，问题是：

1. **golden 模式 assertion 表达力弱**：只能 stdout 字符串对比，无法表达"应抛 X 异常"、"返回值的浮点精度"、"集合元素无序相等"等
2. **Stdlib 改动盲点大**：改 z42.collections 的 LinkedList 不知道触发哪些 golden；可能跑全部 100+ 也未必覆盖
3. **新增 stdlib 库的测试门槛高**：要在 runtime/tests/ 写 golden，远离库源码，增加心智负担

P2 引入：

1. **z42-test-runner**（宿主端工具）：扫描 .zbc 中带 `[Test]` attribute 的方法、调用、收集结果、TAP/JSON 输出
2. **z42.test 库扩展**：补 assertion API（assertEq / assertThrows / assertNear 等）
3. **元数据规范**：`@test-tier` front-matter 标签（决定测试归属，P3 用）
4. **增量测试脚本**：`scripts/test-changed.sh` —— git diff → 受影响测试集

P2 **只交付工具与规范**，不迁移任何现有 golden（迁移留给 P3）。

## What Changes

- **新建 z42-test-runner crate**：`src/toolchain/test-runner/`（占位目录已存在）
  - CLI：`z42-test-runner [paths...] [--format tap|json|pretty] [--filter <regex>]`
  - 测试发现：扫描 .zbc，提取带 `z42.test.Test` attribute 的函数
  - 调用执行：通过 z42 runtime 加载 .zpkg，调用每个测试函数
  - 结果收集：捕获 panic / assertion failure，统一报告
- **z42.test 库扩展**：
  - 新增 `Test` attribute 类型（IR 元数据）
  - 新增 assertion API：`assertEq`、`assertNotEq`、`assertTrue`、`assertFalse`、`assertThrows<E>`、`assertNear`、`fail`
  - 新增 `Skip` / `Ignore` attribute
- **元数据规范** [docs/design/testing.md](docs/design/testing.md)：
  - `// @test-tier: vm_core | stdlib:<lib> | integration` front-matter
  - `[Test]` / `[Skip]` / `[Ignore]` attribute 语义
  - 测试发现规则
  - TAP / JSON 输出格式
- **scripts/test-changed.sh**：根据 git diff 输出受影响测试集
- **just 接入**：`just test-changed` 替换 P0 占位

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml` | MODIFY | `[workspace]` members 加 `src/toolchain/test-runner` |
| `src/toolchain/test-runner/Cargo.toml` | NEW | runner crate manifest |
| `src/toolchain/test-runner/src/main.rs` | NEW | CLI 入口（clap 解析参数） |
| `src/toolchain/test-runner/src/discover.rs` | NEW | 扫描 .zbc 提取 `[Test]` 方法 |
| `src/toolchain/test-runner/src/runner.rs` | NEW | 加载 .zpkg + 调用测试 + 捕获失败 |
| `src/toolchain/test-runner/src/result.rs` | NEW | TestResult / TestSuiteResult 类型 |
| `src/toolchain/test-runner/src/format/mod.rs` | NEW | Formatter trait |
| `src/toolchain/test-runner/src/format/tap.rs` | NEW | TAP 13 输出 |
| `src/toolchain/test-runner/src/format/json.rs` | NEW | JSON 输出（schema 见 design.md） |
| `src/toolchain/test-runner/src/format/pretty.rs` | NEW | TTY 友好输出（带颜色） |
| `src/toolchain/test-runner/tests/integration_test.rs` | NEW | runner 自身的集成测试 |
| `src/toolchain/test-runner/README.md` | NEW | 工具文档 |
| `src/toolchain/README.md` | MODIFY | 子目录列表加 test-runner |
| `src/libraries/z42.test/src/Test.z42` | NEW | `[Test]` / `[Skip]` / `[Ignore]` attribute 定义 |
| `src/libraries/z42.test/src/Assert.z42` | NEW（或 MODIFY） | 全部 assertion API |
| `src/libraries/z42.test/src/Failure.z42` | NEW | TestFailure 异常类型 |
| `src/libraries/z42.test/z42.test.toml` | MODIFY | 暴露 public API |
| `src/libraries/z42.test/README.md` | MODIFY | 文档更新 |
| `src/libraries/z42.test/tests/` | NEW（dir） | z42.test 自测目录占位 |
| `scripts/test-changed.sh` | NEW | git diff → 受影响测试集 |
| `justfile` | MODIFY | 替换 `test-changed` 占位为完整实现 |
| `docs/design/testing.md` | NEW | 元数据规范 + 测试发现 + 输出格式 |
| `docs/design/test-runner.md` | NEW | runner 实现原理 |
| `docs/dev.md` | MODIFY | 加 "z42-test-runner" 段 |
| `src/compiler/z42.IR/Metadata/AttributeKind.cs` | MODIFY（若需） | 注册 `z42.test.Test` 等 attribute kind |

**只读引用**：
- [src/libraries/z42.test/](src/libraries/z42.test/) — 现有 z42.test 库
- [src/runtime/src/](src/runtime/src/) — 理解 .zpkg 加载与函数调用 API
- [src/runtime/src/metadata/](src/runtime/src/metadata/) — 理解 attribute 元数据格式
- [src/compiler/z42.IR/](src/compiler/z42.IR/) — 理解 attribute IR 表示

## Out of Scope

- **golden 用例迁移**（P3）：本 spec 不动 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/)
- **stdlib 各库的实际 tests/ 目录建设**（P3）：本 spec 只搭工具
- **编译器测试改造**：保留 [src/compiler/z42.Tests/](src/compiler/z42.Tests/) C# xUnit 现状
- **并行执行**：runner 初版串行执行测试（避免 stdlib 全局状态问题）；并行留给后续优化
- **测试隔离 / setUp/tearDown 钩子**：本 spec 不引入；后续按需扩展
- **代码覆盖率工具**（如 cargo-tarpaulin / coverlet）：超出范围
- **fuzz / property-based**：z42 端 property-based 测试超出范围；C# 端已有 FsCheck 不动

## Open Questions

- [ ] **Q1**：`[Test]` attribute 的 IR 编码用 string `"z42.test.Test"` 还是分配数字 ID？
  - 倾向：先用 string（与现有 attribute 元数据一致），后期视性能优化
- [ ] **Q2**：runner 如何调用 z42 函数？复用 z42vm 的入口还是新建轻量 host？
  - 倾向：复用 z42vm 的 `Interpreter::call_function` API（避免重复实现）
- [ ] **Q3**：assertion 失败时是抛异常还是返回 Result？
  - 倾向：抛异常（z42.test.Failure），runner 在调用边界 catch
- [ ] **Q4**：`assertNear` 默认 epsilon 多少？
  - 倾向：1e-9（f64），可被参数覆盖
- [ ] **Q5**：增量测试的影响计算范围多深？
  - 倾向：本 spec 只支持 1 层（直接依赖该库的库）；多层依赖图留给后续
