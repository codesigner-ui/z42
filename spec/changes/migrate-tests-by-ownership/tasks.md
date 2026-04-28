# Tasks: Migrate Tests by Ownership

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P0 / P1 / P2 全部完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: 迁移工具
- [ ] 阶段 2: 自动迁移 + 人工 review
- [ ] 阶段 3: 删除老路径 + 更新工具脚本
- [ ] 阶段 4: stdlib 6 库补原生测试
- [ ] 阶段 5: just / CI 矩阵改造
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 验证

---

## 阶段 1: 迁移工具

- [ ] 1.1 [scripts/_migrate-tests.sh](scripts/_migrate-tests.sh) 实现 design.md Decision 3 算法
- [ ] 1.2 该脚本支持 `--dry-run` 输出归属表但不移动
- [ ] 1.3 [scripts/build-stdlib-tests.sh](scripts/build-stdlib-tests.sh) 编译某库的 tests/*.z42 → .zbc

## 阶段 2: 自动迁移 + 人工 review

- [ ] 2.1 跑 `./scripts/_migrate-tests.sh --dry-run`，输出归属表
- [ ] 2.2 人工 review 归属表（预计 5–10 个边界 case 需要修正）
- [ ] 2.3 跑 `./scripts/_migrate-tests.sh` 实际迁移（git mv 保留 history）
- [ ] 2.4 验证：每个迁出 .z42 顶部含 `// @test-tier: ...`
- [ ] 2.5 验证：迁移前后用例数一致（grep -c 比对）

## 阶段 3: 删除老路径 + 更新工具脚本

- [ ] 3.1 `rm -rf src/runtime/tests/golden/run/`
- [ ] 3.2 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 端到端调度逻辑迁出，只留 zbc 契约
- [ ] 3.3 [src/runtime/tests/vm_core/runner.rs](src/runtime/tests/vm_core/runner.rs) 实现 cargo test harness（design.md Decision 5）
- [ ] 3.4 [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh) 适配新路径（递归扫多目录）
- [ ] 3.5 [scripts/test-vm.sh](scripts/test-vm.sh) 改为遍历 `src/runtime/tests/vm_core/`
- [ ] 3.6 [scripts/test-cross-zpkg.sh](scripts/test-cross-zpkg.sh) 改为驱动 `tests/integration/`

## 阶段 4: stdlib 6 库补原生测试

- [ ] 4.1 [src/libraries/z42.core/tests/string_basics.z42](src/libraries/z42.core/tests/string_basics.z42) ≥ 3 个 [Test]
- [ ] 4.2 [src/libraries/z42.collections/tests/linkedlist.z42](src/libraries/z42.collections/tests/linkedlist.z42) ≥ 3 个
- [ ] 4.3 [src/libraries/z42.math/tests/math_basics.z42](src/libraries/z42.math/tests/math_basics.z42) ≥ 3 个
- [ ] 4.4 [src/libraries/z42.io/tests/console.z42](src/libraries/z42.io/tests/console.z42) ≥ 3 个
- [ ] 4.5 [src/libraries/z42.text/tests/stringbuilder.z42](src/libraries/z42.text/tests/stringbuilder.z42) ≥ 3 个
- [ ] 4.6 [src/libraries/z42.test/tests/self.z42](src/libraries/z42.test/tests/self.z42) dogfooding ≥ 3 个
- [ ] 4.7 各 tests/ 目录下补 README.md（按 code-organization 规则）

## 阶段 5: just / CI 矩阵改造

- [ ] 5.1 [justfile](justfile) `test-vm` 改为只跑 vm_core（路径变了）
- [ ] 5.2 [justfile](justfile) `test-stdlib` 替换 P2 占位为完整实现（含编译 .zbc 步骤）
- [ ] 5.3 [justfile](justfile) `test-integration` 替换 P2 占位
- [ ] 5.4 [justfile](justfile) `test` 改为 `test-compiler && test-vm && test-stdlib && test-integration`
- [ ] 5.5 [.github/workflows/ci.yml](.github/workflows/ci.yml) 加 matrix `target` 维度
- [ ] 5.6 各 target job 跑 `just test-<target>`
- [ ] 5.7 `windows-latest` 仍只跑 smoke (compiler + cargo test)

## 阶段 6: 文档同步

- [ ] 6.1 [docs/design/testing.md](docs/design/testing.md) 加"测试目录树"图与归属示例
- [ ] 6.2 [docs/design/testing.md](docs/design/testing.md) 标注 P3 完成
- [ ] 6.3 [docs/dev.md](docs/dev.md) 测试段更新（按归属命令）
- [ ] 6.4 [docs/roadmap.md](docs/roadmap.md) 进度表加 P3 完成
- [ ] 6.5 [.claude/rules/code-organization.md](.claude/rules/code-organization.md) 加测试目录约定
- [ ] 6.6 各 stdlib 库的 README 列 tests/ 内容

## 阶段 7: 验证

- [ ] 7.1 `find src/runtime/tests/golden/run/` 返回空
- [ ] 7.2 `just test` 全绿（compiler + vm_core + 全 stdlib + integration）
- [ ] 7.3 用例数：迁移后 ≥ 109 (= 103 迁移 + 6 各库新增)
- [ ] 7.4 `just test-vm` 仅跑 vm_core，全绿
- [ ] 7.5 `just test-stdlib z42.collections` 仅跑该库
- [ ] 7.6 `just test-integration` 跑通全部跨库用例
- [ ] 7.7 `grep -L '@test-tier' src/**/*.z42 tests/**/*.z42` 输出为空
- [ ] 7.8 `just test-changed` 在仅修改 z42.io 时只触发 z42.io + 下游
- [ ] 7.9 `./scripts/regen-golden-tests.sh` 重生后仍全绿
- [ ] 7.10 CI 5+ jobs 并行全绿
- [ ] 7.11 `dotnet test src/compiler/z42.Tests/` 用例数与迁移前完全一致

## 备注

### 实施依赖

- 必须先完成 P0 / P1 / P2
- 不依赖 P4

### 风险

- **风险 1**：自动迁移工具误判归属 → 加 `--dry-run` + 人工 review 兜底
- **风险 2**：用例迁移破坏 git history → 用 `git mv` 而非 cp/rm
- **风险 3**：编译器 GoldenTests.cs 引用的 .z42 路径变化 → 找出引用并更新（在 4.x 任务中追加）
- **风险 4**：CI matrix 并行后总配额翻倍 → 取消 macOS 上的某些 target（如 integration only on linux）
- **风险 5**：stdlib tests 编译依赖 stdlib 自身已编译 → 编译顺序：先 build-stdlib，再 build-stdlib-tests

### 工作量估计

2–3 天（迁移本身 0.5 天 + review 0.5 天 + 工具更新 0.5 天 + stdlib 测试 0.5 天 + CI 0.5 天 + 文档）。
