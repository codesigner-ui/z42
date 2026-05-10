# Spec: Test Migration by Ownership

## ADDED Requirements

### Requirement: 用例物理位置

#### Scenario: vm_core 用例位置

- **WHEN** 一个 .z42 用例不依赖任何 stdlib 库（或仅用 Console.println 例外）
- **THEN** 该用例位于 `src/runtime/tests/vm_core/<NN_name>/`，含 source.z42、source.zbc、expected_output.txt

#### Scenario: stdlib 用例位置

- **WHEN** 一个 .z42 用例仅 import 一个 stdlib 库 (如 z42.collections)
- **THEN** 该用例位于 `src/libraries/z42.collections/tests/`

#### Scenario: integration 用例位置

- **WHEN** 一个 .z42 用例 import ≥ 2 个 stdlib 库
- **THEN** 该用例位于 `tests/integration/<NN_name>/`

#### Scenario: 老路径已删除

- **WHEN** 检查 `src/runtime/tests/golden/run/`
- **THEN** 目录不存在

---

### Requirement: front-matter 标注

#### Scenario: 所有迁移后用例含 @test-tier

- **WHEN** 在 `src/runtime/tests/vm_core/`、`src/libraries/*/tests/`、`tests/integration/` 中执行 `grep -L '@test-tier' **/*.z42`
- **THEN** 输出为空（每个 .z42 都含 front-matter）

#### Scenario: tier 值与位置一致

- **WHEN** 一个 .z42 在 `src/libraries/z42.io/tests/`
- **THEN** 其 front-matter 含 `// @test-tier: stdlib:z42.io`（库名一致）

---

### Requirement: stdlib 每库最低原生测试

#### Scenario: z42.core 含原生测试

- **WHEN** 检查 `src/libraries/z42.core/tests/`
- **THEN** 含至少 1 个 .z42 文件，至少 3 个 `[Test]` 函数

#### Scenario: 其他 5 库同样含原生测试

- **WHEN** 检查 z42.collections / z42.math / z42.io / z42.text / z42.test 的 `tests/`
- **THEN** 每个目录至少 1 个 .z42 文件，至少 3 个 `[Test]` 函数

#### Scenario: z42.test 自测 (dogfooding)

- **WHEN** 运行 `cargo run -p z42-test-runner -- src/libraries/z42.test/tests/`
- **THEN** 至少 3 个测试通过；含 1 个故意失败的测试验证 runner 能捕获 (skip 标记)

---

### Requirement: 命令行入口

#### Scenario: just test-vm 仅跑 vm_core

- **WHEN** 执行 `just test-vm`
- **THEN** 仅扫描 `src/runtime/tests/vm_core/`；不涉及 stdlib 用例

#### Scenario: just test-stdlib 跑全部 stdlib

- **WHEN** 执行 `just test-stdlib`（无参）
- **THEN** 依次跑 6 个 stdlib 库的 tests/

#### Scenario: just test-stdlib <lib> 跑指定库

- **WHEN** 执行 `just test-stdlib z42.collections`
- **THEN** 仅跑该库 tests/

#### Scenario: just test-integration

- **WHEN** 执行 `just test-integration`
- **THEN** 跑 `tests/integration/` 全部用例（替换 P2 占位）

---

### Requirement: 增量测试归属正确

#### Scenario: 改 z42.io 仅触发 z42.io 与下游

- **WHEN** git diff 仅修改 `src/libraries/z42.io/src/Console.z42`
- **WHEN** 执行 `just test-changed`
- **THEN** 跑 z42.io tests + 依赖 z42.io 的 stdlib tests + integration
- **AND** 不跑 z42.core / z42.math / z42.collections (除非依赖 z42.io)

#### Scenario: 改 vm core 触发全部 stdlib

- **WHEN** git diff 修改 `src/runtime/src/gc/heap.rs`
- **WHEN** 执行 `just test-changed`
- **THEN** 跑全部 stdlib + vm_core + integration（VM 改动可能影响所有库）

---

### Requirement: 编译器测试不动

#### Scenario: src/compiler/z42.Tests/ 保留 xUnit

- **WHEN** 检查 `src/compiler/z42.Tests/`
- **THEN** 项目仍为 xUnit；测试用例数与迁移前一致；不引入 z42-test-runner

#### Scenario: 编译器 GoldenTests.cs 保留

- **WHEN** 检查 `src/compiler/z42.Tests/GoldenTests.cs`
- **THEN** 文件存在且未删除；其引用的 .z42 输入仍可访问（必要时通过相对路径调整指向 vm_core/）

---

### Requirement: CI 矩阵按归属并行

#### Scenario: PR 触发多 job

- **WHEN** PR 触发 CI
- **THEN** 至少 5 个并行 job：compiler / vm_core / stdlib / integration / windows-smoke
- **AND** 每个 job 只跑对应 target，不跨

#### Scenario: 单 job 失败定位精准

- **WHEN** 任一 target job 失败
- **THEN** GitHub PR 检查显示失败 job 名（如 `ubuntu / target=stdlib`）
- **AND** 开发者本地跑对应 `just test-<target>` 即可复现

---

### Requirement: 工具更新

#### Scenario: regen-golden-tests.sh 适配新路径

- **WHEN** 执行 `./scripts/regen-golden-tests.sh`
- **THEN** 重生所有迁移后位置的 .zbc（vm_core / 各 stdlib / integration）；不漏任何用例

#### Scenario: scripts/test-vm.sh 仅扫 vm_core

- **WHEN** 执行 `./scripts/test-vm.sh`
- **THEN** 只迭代 `src/runtime/tests/vm_core/`，行为与 just test-vm 等价

#### Scenario: zbc_compat.rs 仅留契约测试

- **WHEN** 阅读 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs)
- **THEN** 文件只剩跨语言 zbc 解码契约；端到端 golden 调度迁出

---

### Requirement: 文档同步

#### Scenario: testing.md 标注迁移完成

- **WHEN** 阅读 [docs/design/testing.md](docs/design/testing.md)
- **THEN** 含"测试目录树"图与各 tier 用例分布示意

#### Scenario: 各 stdlib 库 README 列 tests/ 内容

- **WHEN** 阅读 `src/libraries/<lib>/tests/README.md`
- **THEN** 列出该目录下所有测试文件主题
