# Proposal: Migrate Tests by Ownership

## Why

P0–P2 把基础设施（just / benchmark / z42-test-runner / 元数据规范）搭好后，103 个 golden 用例仍然全部堆在 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/)。这导致：

1. **改 stdlib 不知影响范围**：改 z42.io 不知道触发哪些 golden（可能跑全部 100+ 仍漏覆盖）
2. **stdlib 各库无本地测试**：6 个库源码目录下空空如也
3. **`just test changed` 无意义**：因为所有用例物理上在同一目录，无法按归属切分
4. **编译器 golden 与 runtime golden 部分重复**（[src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs)），未消歧

P3 的工作是**实质迁移**：

- 给 103 个用例**逐个**加 `@test-tier` front-matter
- 按 tier 物理移到对应目录
- stdlib 各库**至少新增 1 个原生测试**（非迁移而来，验证 z42-test-runner 能跑通该库）
- 删除老的 `src/runtime/tests/golden/run/`

P3 是体力活，但是 **P0+P1+P2 投入的回报兑现点** —— 之后增量测试与基线对比才真正有意义。

## What Changes

- **103 个 golden 用例迁移**：每个 `.z42` 加 front-matter，按 tier 移到：
  - `src/runtime/tests/vm_core/`（不依赖 stdlib 的算术 / 控制流 / 类 / 异常）
  - `src/libraries/<lib>/tests/`（仅依赖单个 stdlib 库）
  - `tests/integration/`（依赖 ≥ 2 个 stdlib，或跨 zpkg）
- **stdlib 6 库各补 ≥ 1 个原生测试**：覆盖该库核心 API，由 z42-test-runner 调度
- **更新 [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh)**：适配新路径
- **更新 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs)**：路径引用迁到 `vm_core/`
- **删除老路径** `src/runtime/tests/golden/run/`
- **just 接入**：`just test-integration` 替换 P2 占位
- **CI 接入**：CI 改为按归属并行跑（compiler / vm_core / 各 stdlib / integration 平行 5+ jobs）
- **文档更新**：[docs/design/testing.md](docs/design/testing.md) 加迁移完成状态与目录树

## Scope

### 新增目录与文件

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/tests/vm_core/` | NEW (dir) | VM 端到端测试根（迁入目标） |
| `src/runtime/tests/vm_core/README.md` | NEW | 目录 README |
| `src/runtime/tests/vm_core/runner.rs` | NEW | cargo test harness（替代 zbc_compat 的迁入子集） |
| `src/runtime/tests/vm_core/<NN_name>/source.z42` | NEW (×N) | 迁入的纯 VM 用例（估 ~30–40 个） |
| `src/runtime/tests/vm_core/<NN_name>/source.zbc` | NEW (×N) | 重生的 .zbc |
| `src/runtime/tests/vm_core/<NN_name>/expected_output.txt` | NEW (×N) | 期望输出 |
| `src/libraries/z42.core/tests/` | NEW (dir) | 该库测试根 |
| `src/libraries/z42.core/tests/README.md` | NEW | 目录 README |
| `src/libraries/z42.core/tests/<topic>.z42` | NEW (×N) | 迁入 + 新增的 z42.core 测试 |
| `src/libraries/z42.collections/tests/` 等 6 个目录同上 | NEW | 同上 |
| `tests/integration/` | NEW (dir) | 跨 stdlib 测试根 |
| `tests/integration/README.md` | NEW | 目录 README |
| `tests/integration/<NN_name>/source.z42` | NEW (×N) | 迁入的跨库用例 |
| `tests/integration/<NN_name>/expected_output.txt` | NEW (×N) | 期望输出 |
| `tests/README.md` | NEW | 顶层 tests 目录 README |

### 修改与删除

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/tests/golden/run/**` | DELETE | 全部 103 个用例目录迁出后删除 |
| `src/runtime/tests/zbc_compat.rs` | MODIFY | 引用路径改为 `vm_core/` 与各 stdlib `tests/`；保留跨语言契约测试核心 |
| `src/runtime/tests/README.md` | NEW（若无） / MODIFY | 说明新分布 |
| `scripts/regen-golden-tests.sh` | MODIFY | 适配新路径（递归扫多个目录） |
| `scripts/test-vm.sh` | MODIFY | 改为遍历 `vm_core/` |
| `justfile` | MODIFY | `test-integration` 替换 P2 占位 |
| `.github/workflows/ci.yml` | MODIFY | CI matrix 加 `test-target` 维度（compiler / vm_core / stdlib-<lib> / integration） |
| `docs/design/testing.md` | MODIFY | 标注 P3 完成；加目录树图 |
| `docs/design/testing.md` | MODIFY | 迁移完成状态记录 |
| `docs/dev.md` | MODIFY | 测试命令段更新（按归属跑） |

**只读引用**：
- 全部 103 个现有 .z42 / .zbc / expected_output.txt（理解归属判定）
- [src/libraries/](src/libraries/) 各库的源码（理解依赖关系）
- [docs/design/testing.md](docs/design/testing.md) — P2 已建立的规范

## Out of Scope

- **编译器 golden 与 runtime golden 合并**：本 spec 不动 [src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs)；自举完成后单独评估
- **新增大量测试用例**：本 spec 只迁移 + 补每库最低 1 个原生测试（共 ~6 个新增）
- **优化 golden 用例本身**（如重复用例合并、命名重命名）：本 spec 保持原内容
- **测试覆盖率工具集成**：超出范围
- **跨平台测试矩阵**：P4 范围

## Open Questions

- [ ] **Q1**：迁移期间老路径与新路径短期共存还是一次切换？
  - 倾向：**一次切换**（pre-1.0 不留兼容；CI 一次性绿）
- [ ] **Q2**：`vm_core/` 用例的目录形式保持 `<NN_name>/source.z42` + `expected_output.txt`，还是改为单文件 + assertion？
  - 倾向：**保持现有格式**（迁移成本低；新写的纯 vm_core 用例也用此格式）
- [ ] **Q3**：z42.test 库自身是否也要补 tests/？
  - 倾向：**是**，用 z42-test-runner 自举（dogfooding）
- [ ] **Q4**：依赖 z42.core 的用例算 stdlib:z42.core 还是 vm_core？
  - 倾向：**z42.core**（z42.core 不是 VM 内置，是普通 stdlib 库）；只有不依赖**任何** stdlib 的才是 vm_core
- [ ] **Q5**：CI 并行 job 数（按归属拆）是否会拖慢总时长（每个 job 启动开销）？
  - 倾向：5–7 个并行 job 收益 > 启动开销；实测后调
