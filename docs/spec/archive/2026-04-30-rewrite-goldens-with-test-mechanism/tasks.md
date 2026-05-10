# Tasks: Rewrite Goldens with Test Mechanism

> 状态：🟢 已完成（minimal 版） | 创建：2026-04-29 | 归档：2026-04-30
>
> **实际交付（与 DRAFT 差异）**：
> - ✅ 阶段 4：6 个 stdlib 库各 1 个原生 `[Test]` 测试文件（z42.math / z42.text / z42.collections / z42.core / z42.io / z42.test 自检）
> - ✅ 半归属物理迁移（独立 spec ad-hoc 落地 2026-04-30）：13 个 stdlib-bound golden 从 `src/runtime/tests/golden/run/` 迁到 `src/libraries/<lib>/tests/golden/`；VM-only 93 例原地保留
> - ⏸️ 阶段 1-3：批量 `_rewrite-goldens.py` 半自动重写工具 —— **未实施**。决定保留 stdout-based 比对（迁移而非重写），节省 ~106 个用例的人工 review 成本
> - ⏸️ 阶段 5：编译器 GoldenTests 引用更新 —— 当前 paths 仍指向原 runtime 目录的 VM-only 部分，不受影响

## 进度概览

- [ ] 阶段 1: 半自动转换工具
- [ ] 阶段 2: 跑工具 + 人工 review
- [ ] 阶段 3: 删老路径 + 工具脚本更新
- [ ] 阶段 4: stdlib 各库补原生测试
- [ ] 阶段 5: 编译器 GoldenTests 引用更新
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 验证

---

## 阶段 1: 转换工具

- [ ] 1.1 [scripts/_rewrite-goldens.py](scripts/_rewrite-goldens.py) 实现 design.md Decision 3 的算法
- [ ] 1.2 工具支持 `--dry-run` 输出归属表 + 分类但不写入
- [ ] 1.3 [scripts/build-stdlib-tests.sh](scripts/build-stdlib-tests.sh) 编译某 lib tests/*.z42 → .zbc

## 阶段 2: 自动迁移 + 人工 review

- [ ] 2.1 跑 `python3 scripts/_rewrite-goldens.py --dry-run` 输出归属与分类表
- [ ] 2.2 人工 review 表（预期 5-15 case 进 manual list）
- [ ] 2.3 跑 `python3 scripts/_rewrite-goldens.py` 实际生成（auto-generated 标注）
- [ ] 2.4 review 自动生成的文件，删 `// AUTO-GENERATED REVIEW REQUIRED` 标注表示已 review
- [ ] 2.5 manual list 用例手工重写
- [ ] 2.6 验证：`grep -L '@test-tier' ...` 返回空

## 阶段 3: 删老路径 + 工具更新

- [ ] 3.1 `rm -rf src/runtime/tests/golden/run/`
- [ ] 3.2 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 端到端调度迁出，仅留 zbc 契约
- [ ] 3.3 [src/runtime/tests/vm_core/runner.rs](src/runtime/tests/vm_core/runner.rs) 新建 cargo harness
- [ ] 3.4 [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh) 适配新路径（vm_core + 各 stdlib tests + integration）
- [ ] 3.5 [scripts/test-vm.sh](scripts/test-vm.sh) 改为遍历 vm_core/
- [ ] 3.6 [scripts/test-cross-zpkg.sh](scripts/test-cross-zpkg.sh) 改为 z42-test-runner tests/integration/

## 阶段 4: stdlib 各库原生测试

- [ ] 4.1 [src/libraries/z42.core/tests/string_basics.z42](src/libraries/z42.core/tests/string_basics.z42) ≥ 3 [Test]
- [ ] 4.2 [src/libraries/z42.collections/tests/linkedlist.z42](src/libraries/z42.collections/tests/linkedlist.z42) ≥ 3 [Test]（如未从迁移得到）
- [ ] 4.3 [src/libraries/z42.math/tests/math_basics.z42](src/libraries/z42.math/tests/math_basics.z42) ≥ 3 [Test]
- [ ] 4.4 [src/libraries/z42.io/tests/console.z42](src/libraries/z42.io/tests/console.z42) ≥ 3 [Test]
- [ ] 4.5 [src/libraries/z42.text/tests/stringbuilder.z42](src/libraries/z42.text/tests/stringbuilder.z42) ≥ 3 [Test]
- [ ] 4.6 [src/libraries/z42.test/tests/self.z42](src/libraries/z42.test/tests/self.z42) dogfood ≥ 3 [Test]
- [ ] 4.7 各 tests/ 目录补 README.md

## 阶段 5: 编译器 GoldenTests 引用更新

- [ ] 5.1 grep [src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs) 引用 `golden/run/` 的路径
- [ ] 5.2 改为 `src/runtime/tests/vm_core/`（仅 vm_core 范围）
- [ ] 5.3 验证：`dotnet test src/compiler/z42.Tests/` 仍全绿

## 阶段 6: 文档

- [ ] 6.1 [docs/design/testing.md](docs/design/testing.md) 标 R5 完成；加迁移后目录树图
- [ ] 6.2 [tests/README.md](tests/README.md) 顶层目录说明
- [ ] 6.3 [src/runtime/tests/README.md](src/runtime/tests/README.md) 更新 vm_core 子目录说明
- [ ] 6.4 各 stdlib 库 README.md 加 tests/ 说明
- [ ] 6.5 [docs/dev.md](docs/dev.md) 测试段更新（按归属命令）
- [ ] 6.6 [docs/roadmap.md](docs/roadmap.md) Pipeline 进度更新

## 阶段 7: 验证

- [ ] 7.1 `find src/runtime/tests/golden/run/` 返回空
- [ ] 7.2 `just test` 全绿（编译器 + vm_core + 6 个 stdlib + integration）
- [ ] 7.3 用例总数 ≥ 109
- [ ] 7.4 `just test-vm` 仅跑 vm_core，全绿
- [ ] 7.5 `just test-stdlib z42.collections` 仅跑该库
- [ ] 7.6 `just test-integration` 全绿
- [ ] 7.7 `grep -L '@test-tier'` 在所有 tests 目录返回空
- [ ] 7.8 `just test-changed` 在仅修改 z42.io 时只触发 z42.io + integration
- [ ] 7.9 `./scripts/regen-golden-tests.sh` 重生后全绿
- [ ] 7.10 `dotnet test src/compiler/z42.Tests/` 全绿（GoldenTests 引用已更新）
- [ ] 7.11 跨平台 vm_core 子集（5 个最简）可被 P4.2/P4.3/P4.4 复用作 consistency test set

## 备注

### 实施依赖

- R1 (test metadata) 必须落地
- R2 (z42.test 库) 必须落地
- R3 (test-runner) 必须落地
- R4 (attribute 校验) 必须落地（避免重写后才发现签名错误）
- 不依赖 P4

### 风险

- **风险 1**：自动转换误判分类 → 人工 review 兜底；预算 5-15 case manual
- **风险 2**：用例迁移破坏 git history → `git mv` 而非 `cp + rm`
- **风险 3**：编译器 GoldenTests 引用路径多 → 阶段 5 grep 找全
- **风险 4**：`@test-tier` 注释格式与某些 .z42 处理不兼容 → 用 line comment 已是 z42 内置
- **风险 5**：build-stdlib-tests.sh 依赖 stdlib 已 build → 顺序：先 build-stdlib，再 build-stdlib-tests
- **风险 6**：dogfooding (z42.test/tests/self.z42) 启动顺序问题 → R3 runner 启动时 z42.test 已先 link
- **风险 7**：超过 50% manual review → 接受手工兜底；总工作量增 1 天

### 工作量估计

3-5 天：
- 转换工具：1 天
- 自动迁移 + 人工 review（103 用例 × 平均 5 min review = 8h）：1 天
- 工具脚本更新（zbc_compat / test-vm.sh / regen / build-stdlib-tests）：0.5 天
- stdlib 各库新增原生测试：0.5 天
- 编译器 GoldenTests 引用更新：0.5 天
- 文档：0.5 天
- 集成验证 + 调试：0.5-1 天

总计约 4 天，含浮动空间。
