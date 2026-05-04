# Tasks: fix-test-vm-stale-artifacts

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04

**变更说明**：`./scripts/test-vm.sh` 入口处自动调用 `build-stdlib.sh` + `regen-golden-tests.sh`，杜绝 stale stdlib zpkg / 旧 golden .zbc 导致的"假绿/假红"测试结果。

**原因**：2026-05-04 排查 D-10（13_assert string.Contains dispatch 误指 LinkedList）时实测发现：

- `artifacts/z42/libs/z42.core.zpkg`（VM 实际加载路径）比 `artifacts/libraries/z42.core/dist/z42.core.zpkg`（最新 build 输出）旧 21 分钟，导致 VM 跑的是旧 stdlib IR
- 多个 multicast/event golden test 的 `source.zbc` 也是旧编译器产出，与新 stdlib + 新 VM IR 形态不匹配
- 二者叠加：原本 D-10 单条假红（其实是 D-9 commit 已修但 libs 没同步），同步 stdlib 后**额外 7 个 multicast/event 假红被释出**（其实是 golden zbc 旧）
- 重新跑 `regen-golden-tests.sh` + `build-stdlib.sh` 后全部恢复全绿（246/246）
- 严重隐患：过去多个归档 commit 的"./scripts/test-vm.sh 全绿"信号是基于 stale artifact 的，可信度受损 —— workflow.md 的"全绿才能 commit" 规则被流程缺口架空

**文档影响**：

- `docs/dev.md`：测试入口指引更新，强调统一用 `test-vm.sh`，前置自动化
- `docs/deferred.md`：D-10 移除并附注（实际由 D-9 commit + 本 fix 联合解决，不存在真实 dispatch bug）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/test-vm.sh`            | MODIFY | cargo build 后、跑测试前，自动调用 `build-stdlib.sh` 与 `regen-golden-tests.sh` |
| `scripts/regen-golden-tests.sh` | MODIFY | 入口处自动调用 `build-stdlib.sh`，确保 stdlib zpkgs 是最新（独立运行也安全；且 user golden import stdlib 类型时不会因为缺 zpkg 编译失败） |
| `docs/dev.md`                   | MODIFY | 测试入口指引：统一 `test-vm.sh`，废弃手动编译/regen 顺序提示 |
| `docs/deferred.md`              | MODIFY | 移除 D-10 条目，加一行 footer 备注 |

**只读引用**：

- `scripts/build-stdlib.sh` — 理解其内部已经自动 build driver（dotnet run --project z42.Driver），无需在 test-vm.sh 重复 dotnet build
- `.claude/rules/workflow.md` — 复核"全绿才能 commit"规则，本 fix 是流程缺口修补，不改 workflow 本身

## 设计要点

1. **顺序**：cargo build (VM) → build-stdlib.sh (compiler + stdlib zpkgs + sync libs/) → regen-golden-tests.sh (user golden zbc) → 跑测试
2. **可选 skip**：加 `--no-rebuild` flag（dev 反复跑同一测试时跳过前置），但默认行为是全量重建
3. **错误传播**：任一前置步骤失败 → test-vm.sh 立即退出 1，不进 cargo run
4. **regen-golden-tests.sh 独立可用性**：单独被人调用时也要保证 stdlib 是最新（防止"我只想 regen golden 但 stdlib 旧" 的二次踩坑）

## 进度

- [x] 1.1 修改 `scripts/test-vm.sh`：在 cargo build 后增加 `./scripts/build-stdlib.sh` 与 `./scripts/regen-golden-tests.sh` 调用，并支持 `--no-rebuild` flag
- [x] 1.2 修改 `scripts/regen-golden-tests.sh`：入口处自动调用 `./scripts/build-stdlib.sh`，并支持 `--no-stdlib` flag（避免 test-vm.sh 触发时 stdlib 重复 build；以及单独运行时默认安全）
- [x] 1.3 更新 `docs/dev.md` 测试入口段落
- [x] 1.4 更新 `docs/deferred.md` 移除 D-10 条目 + 加 footer 备注
- [x] 1.5 验证 stale 自动恢复：人为破坏 `artifacts/z42/libs/z42.core.zpkg` mtime 后跑 test-vm.sh 应自动恢复 + 全绿
- [x] 1.6 GREEN：`dotnet test src/compiler/z42.Tests/z42.Tests.csproj` + `./scripts/test-vm.sh` 全绿

## 备注

- 这个 fix 处理的是"测试入口工程缺口"，不是任何语言/IR/VM 行为变更，按 workflow 是 fix 类型最小化模式
- 不涉及代码生成 / 类型系统 / VM 指令变更，无需 spec/specs/ + design.md
- 完成后立即解锁 P1–P5 deferred 项的实施
