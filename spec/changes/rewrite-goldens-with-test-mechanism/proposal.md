# Proposal: Rewrite Goldens with [Test] + Assert Mechanism

## Why

103 个 golden 用例集中在 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/)，全部用 stdout 字面量比对。R1+R2+R3+R4 把测试框架（编译时发现 + Assert/TestIO + runner + 校验）建好后，**保留 stdout 模式而不重写** 是浪费。

R5 一次性完成：
1. 按归属物理分流（替代原 [migrate-tests-by-ownership](../migrate-tests-by-ownership/) (P3)，已 SUPERSEDED）
2. 把 stdlib / integration tier 的用例**重写**为 `[Test]` + `Assert` 形式
3. vm_core tier 保留 stdout-based（避免 z42.test 循环依赖）
4. stdlib 各库补 ≥ 1 个原生测试

收益：每个用例细粒度 assertion；失败定位精确；新增 case 几乎零成本；与 R3 runner 自然集成。

## What Changes

### 归属规则（同 P3 SUPERSEDED 设计）

| Tier | 判定 | 测试框架 |
|------|------|---------|
| `vm_core` | 不依赖 stdlib（或仅 Console.WriteLine 例外） | stdout-based golden（cargo test） |
| `stdlib:<lib>` | 仅依赖单个 stdlib 库 | `[Test]` + Assert（z42-test-runner） |
| `integration` | 依赖 ≥ 2 个 stdlib | `[Test]` + Assert + `TestIO.captureStdout` |

### 重写转换规则（按 golden 类型）

R5 的核心。详见 design.md。简言之：
- **类型 A：仅 println 字面量** → 保 stdout-based 进 vm_core，或重写为 captureStdout assert
- **类型 B：算术 + 打印结果** → 重写为多个 [Test]，每个测一个边界 case
- **类型 C：异常路径** → 重写为 `[Test][ShouldThrow<E>]` 或 `Assert.throws<E>`
- **类型 D：stdlib API 验证** → 重写为多个 [Test]，每个测一个 API 行为
- **类型 E：跨 stdlib** → 重写到 tests/integration/，多个 [Test]

### 半自动转换工具

[scripts/_rewrite-goldens.py](scripts/_rewrite-goldens.py) 一次性脚本：

1. 解析每个 source.z42 + expected_output.txt
2. 启发式分类（看 import / println 内容 / 异常关键字）
3. 生成新的 .z42 文件骨架（含 `// AUTO-GENERATED REVIEW REQUIRED` 标记）
4. 自动设置归属目录路径
5. 标无法分类的入 manual review list

### 工作量估计

- 自动转换：~70% 用例可自动生成 [Test] 骨架
- 人工 review + 拆细：~20% 用例需手动调整
- 完全手写：~10% 用例（复杂 case）

### 之后状态

- [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/) **删除**（pre-1.0 不留兼容）
- vm_core 用例进 `src/runtime/tests/vm_core/<NN>/source.z42` 保留 source.z42 + expected_output.txt 格式
- stdlib 用例进 `src/libraries/<lib>/tests/<topic>.z42` 用 `[Test]` + Assert
- integration 用例进 `tests/integration/<topic>.z42` 用 `[Test]` + Assert
- 编译器 golden（[src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs)）**保留 C# xUnit**（自举完成前不动）

## Scope

### 新增

| 路径 | 内容 | 估计数量 |
|------|------|---------|
| `src/runtime/tests/vm_core/runner.rs` | cargo test harness 替代旧 zbc_compat 部分 | 1 |
| `src/runtime/tests/vm_core/<NN>_<name>/{source.z42,source.zbc,expected_output.txt}` | 迁入的 vm_core 用例 | ~30-40 |
| `src/libraries/z42.core/tests/<topic>.z42` | 重写后的 stdlib:z42.core 用例 | ~10-15 |
| `src/libraries/z42.collections/tests/<topic>.z42` | 同 z42.collections | ~10-15 |
| `src/libraries/z42.io/tests/<topic>.z42` | 同 z42.io | ~5-10 |
| `src/libraries/z42.math/tests/<topic>.z42` | 同 z42.math | ~5-10 |
| `src/libraries/z42.text/tests/<topic>.z42` | 同 z42.text | ~5-10 |
| `src/libraries/z42.test/tests/<topic>.z42` | dogfooding | ~3-5 |
| `tests/integration/<NN>_<name>.z42` | 跨 stdlib 用例 | ~10-20 |
| `tests/README.md` | 顶层目录说明 | 1 |
| `scripts/_rewrite-goldens.py` | 半自动转换器（一次性） | 1 |
| `scripts/build-stdlib-tests.sh` | 编译每库 tests/*.z42 → .zbc | 1 |

### 修改

| 路径 | 变更 |
|------|------|
| `src/runtime/tests/zbc_compat.rs` | 端到端调度逻辑迁出，只留 zbc 跨语言契约 |
| `scripts/regen-golden-tests.sh` | 适配新路径 |
| `scripts/test-vm.sh` | 改为遍历 vm_core/ |
| `scripts/test-cross-zpkg.sh` | 改为驱动 tests/integration/ |
| `src/runtime/tests/golden/run/` | **删除整个目录** |
| 各 stdlib 库 README.md | 加 tests/ 说明 |
| `docs/design/testing.md` | 标 R5 完成；更新目录树 |
| `docs/roadmap.md` | 进度表 |

**只读引用**：
- 全部 103 个现有 .z42 / expected_output.txt（用于分类与重写）
- [add-test-metadata-section/](../add-test-metadata-section/) (R1)
- [extend-z42-test-library/](../extend-z42-test-library/) (R2)
- [rewrite-z42-test-runner-compile-time/](../rewrite-z42-test-runner-compile-time/) (R3)
- [compiler-validate-test-attributes/](../compiler-validate-test-attributes/) (R4)
- [src/libraries/](src/libraries/) 各库源码（理解依赖关系）

## Out of Scope

- **编译器 golden**（[src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs)）：保留不动
- **CI 矩阵改造**：留独立任务（R5 完成后）
- **新增大量测试**：本期只迁移 + 每库最低 1 个原生测试
- **测试覆盖率工具**：v0.3
- **doc-test 提取**：v0.2

## Open Questions

- [ ] **Q1**：100+ 用例迁移期间，测试一次性切换还是分批？
  - 倾向：**一次性切换**（pre-1.0 不留兼容；CI 一次性绿）
- [ ] **Q2**：自动转换器误判率高（< 50% 自动通过）时怎么办？
  - 倾向：接受手工兜底（最多 ~50 个用例 manual review，每个 ~10 分钟，总 8h）
- [ ] **Q3**：vm_core 用例的 front-matter 仍加 `// @test-tier: vm_core`？
  - 倾向：是（统一规范，便于增量测试脚本识别）
- [ ] **Q4**：tests/integration/ 用 stdout-based 还是 [Test]?
  - 倾向：[Test] + TestIO.captureStdout（统一与 stdlib 同模式；可 import 多 stdlib）
- [ ] **Q5**：是否在 R5 中一并改造 CI matrix（按归属并行）？
  - 倾向：**留独立任务**（R5 完成后立 R5.1 单独搞 CI；本期重点是迁移）
