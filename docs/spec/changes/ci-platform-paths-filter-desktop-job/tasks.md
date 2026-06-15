# Tasks: ci-platform-paths-filter-desktop-job

> 状态：🟡 进行中 | 创建：2026-06-16 | 类型：infra (CI)

**变更说明：** 平台测试流程两项迭代——① 给平台 job 加 paths 过滤(dorny/paths-filter +
nightly cron 兜底)止住"每次 push 全跑 + 被并行 push 狂取消"；② 新增 test-desktop CI job
补齐四平台 dashboard。
**原因：** 平台 job(各 15–25min)在每个 commit 无条件全触发，烧 CI + churn；desktop 后端
本地 7/7 但没进 CI。
**并行：** 占 toolchain;User 授权。仅改 .github/workflows/ci.yml（+ 可能 desktop 报告步骤）。
**验证：** YAML actionlint + push 后观察 Actions(paths 过滤行为只能远程验证)。

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `.github/workflows/ci.yml` | MODIFY | 加 `changes`(paths-filter) job + `schedule` cron；test-wasm/ios/android/desktop 加 `if` 门；新增 test-desktop job |
| `docs/spec/changes/ACTIVE.md` | MODIFY | toolchain 登记/释放 |

## 任务
- [ ] 1.1 加 `changes` job（dorny/paths-filter@v3）输出 `platform`（src/runtime/**、src/toolchain/host/**、examples/embedding/**、scripts/xtask_test_platform*、ci.yml）
- [ ] 1.2 加 `schedule:` cron（nightly 全量兜底）
- [ ] 1.3 test-wasm/ios/android 加 `needs: changes` + `if: changes.platform=='true' || schedule`
- [ ] 1.4 新增 test-desktop job（ubuntu，bootstrap + `test platform desktop` + dorny report + upload junit），同 `if` 门
- [ ] 1.5 actionlint 通过 + push + 观察:platform job 在非平台改动 commit 上跳过、平台改动/nightly 上跑、desktop job 绿
- [ ] 1.6 归档 + commit

## 备注
- 取舍:z42c-only commit 不触发平台测试 → nightly cron 兜底覆盖（含 src/libraries 等未进 per-push 过滤的面）
- desktop harness 的 _nativeLibs() 有 Linux 分支 → ubuntu runner 可跑
