# Spec: Rewrite Goldens with Test Mechanism

## ADDED Requirements

### Requirement: 测试分流到 3 个 tier

#### Scenario: vm_core 用例位置

- **WHEN** 一个 .z42 用例不依赖任何 stdlib（或仅 Console.WriteLine 例外）
- **THEN** 位于 `src/runtime/tests/vm_core/<NN>_<name>/`（含 source.z42 + source.zbc + expected_output.txt）

#### Scenario: stdlib 用例位置

- **WHEN** 仅 import 单个 stdlib 库
- **THEN** 位于 `src/libraries/<lib>/tests/<topic>.z42`，**重写为 [Test] + Assert**

#### Scenario: integration 用例位置

- **WHEN** import ≥ 2 个 stdlib 库
- **THEN** 位于 `tests/integration/<NN>_<name>.z42`，重写为 [Test] + Assert + TestIO.captureStdout

#### Scenario: 老路径已删

- **WHEN** 检查 `src/runtime/tests/golden/run/`
- **THEN** 目录不存在

---

### Requirement: vm_core 保 stdout 模式

#### Scenario: 文件格式

- **WHEN** vm_core 下用例
- **THEN** 含 `source.z42` + `source.zbc` + `expected_output.txt` 三个文件
- **AND** `source.z42` 不 import z42.test
- **AND** `source.z42` 顶部含 `// @test-tier: vm_core`

#### Scenario: cargo test 调度

- **WHEN** 执行 `just test-vm`
- **THEN** 走 `src/runtime/tests/vm_core/runner.rs` 的 cargo harness
- **AND** 不调用 z42-test-runner

---

### Requirement: stdlib 重写为 [Test]

#### Scenario: 至少含 [Test] 函数

- **WHEN** 任一 `src/libraries/<lib>/tests/*.z42` 文件
- **THEN** 至少含 1 个 `[Test]` 函数
- **AND** 顶部 `// @test-tier: stdlib:<lib>`

#### Scenario: 使用 Assert API

- **WHEN** 测试体内
- **THEN** 用 `Assert.eq` / `Assert.throws<E>` / `Assert.near` 等
- **AND** 不再用 `Console.WriteLine` + stdout 比对

#### Scenario: 异常路径用 ShouldThrow 或 throws

- **WHEN** 测试预期抛 X 异常
- **THEN** 用 `[ShouldThrow<X>]` 标注或测试体内 `Assert.throws<X>(|| { ... })`

---

### Requirement: integration 重写

#### Scenario: 至少含 [Test] 函数

- **WHEN** 任一 `tests/integration/*.z42`
- **THEN** 含 1+ `[Test]`
- **AND** 顶部 `// @test-tier: integration`

#### Scenario: 多 stdlib 协同测试

- **WHEN** integration 测试需要验证跨库行为（如 collection + io）
- **THEN** 用 TestIO.captureStdout 捕获，再 Assert.eq 比对内容

---

### Requirement: stdlib 每库最低原生测试

#### Scenario: z42.core 含 string_basics.z42

- **WHEN** 检查 `src/libraries/z42.core/tests/`
- **THEN** 含 `string_basics.z42`（或等价文件）覆盖 string concat / length / substring
- **AND** 至少 3 个 `[Test]` 函数

#### Scenario: z42.collections 含 linkedlist.z42

- **WHEN** 检查 `src/libraries/z42.collections/tests/`
- **THEN** 含 LinkedList 核心 API 测试（≥ 3 个 [Test]）

#### Scenario: z42.math / z42.io / z42.text / z42.test 各含原生测试

- **WHEN** 检查各库 tests/
- **THEN** 每库至少 1 个测试文件，至少 3 个 [Test]

---

### Requirement: front-matter 标注

#### Scenario: 所有迁移后 .z42 含 @test-tier

- **WHEN** `grep -L '@test-tier' src/runtime/tests/vm_core/**/*.z42 src/libraries/*/tests/*.z42 tests/integration/*.z42`
- **THEN** 输出为空

#### Scenario: tier 值与位置一致

- **WHEN** 一个 .z42 在 `src/libraries/z42.io/tests/`
- **THEN** 其 front-matter 含 `// @test-tier: stdlib:z42.io`

---

### Requirement: 半自动转换工具

#### Scenario: 工具一次性运行

- **WHEN** `python3 scripts/_rewrite-goldens.py`
- **THEN** 自动处理 ≥ 70% 用例（生成 .z42 骨架并放正确位置）
- **AND** 输出 manual review list（< 30% 用例）

#### Scenario: 自动生成的标注

- **WHEN** 工具生成的 .z42 文件
- **THEN** 顶部含 `// AUTO-GENERATED REVIEW REQUIRED` 注释（review 后 reviewer 删除）

---

### Requirement: 工具脚本更新

#### Scenario: regen-golden-tests.sh 适配新路径

- **WHEN** `./scripts/regen-golden-tests.sh`
- **THEN** 重生 vm_core/ + 各 stdlib tests/ + integration/ 全部 .zbc，全绿

#### Scenario: test-vm.sh 仅扫 vm_core

- **WHEN** `./scripts/test-vm.sh`
- **THEN** 只迭代 `src/runtime/tests/vm_core/`

#### Scenario: test-cross-zpkg.sh 改用 z42-test-runner

- **WHEN** `./scripts/test-cross-zpkg.sh`
- **THEN** 调 `z42-test-runner tests/integration/`

#### Scenario: zbc_compat.rs 仅留契约

- **WHEN** 阅读 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs)
- **THEN** 文件只剩跨语言 zbc 解码契约；端到端 golden 调度迁出

---

### Requirement: 工程集成

#### Scenario: just test 全绿

- **WHEN** R5 完成后 `just test`
- **THEN** 全绿（compiler + vm_core + stdlib × 6 + integration）
- **AND** 用例总数 ≥ 109

#### Scenario: just test-changed 归属正确

- **WHEN** git diff 仅 src/libraries/z42.io/，执行 `just test-changed`
- **THEN** 跑 z42.io tests + integration（依赖 z42.io 的）；不跑其他 stdlib

#### Scenario: 编译器 GoldenTests 引用更新

- **WHEN** 阅读 `src/compiler/z42.Tests/GoldenTests.cs`
- **THEN** 引用路径已从 `src/runtime/tests/golden/run/` 更新为 `src/runtime/tests/vm_core/`
- **AND** `dotnet test src/compiler/z42.Tests/` 全绿

#### Scenario: 编译器 xUnit 框架不变

- **WHEN** 阅读 `src/compiler/z42.Tests/`
- **THEN** 仍是 xUnit；测试用例数与迁移前一致

---

### Requirement: 文档

#### Scenario: testing.md 标记 R5 完成

- **WHEN** 阅读 [docs/design/testing.md](docs/design/testing.md)
- **THEN** R5 段标"已完成"
- **AND** 含 R5 实施后目录树图

#### Scenario: 各库 README 列 tests/

- **WHEN** 阅读 `src/libraries/<lib>/tests/README.md`
- **THEN** 列出该目录下测试文件主题

#### Scenario: tests/README.md 总览

- **WHEN** 阅读 [tests/README.md](tests/README.md)
- **THEN** 含 integration tier 说明 + 与 vm_core / stdlib tests 的关系
