# Tasks: migrate-scripts-to-z42

> 状态：🟡 进行中 | 创建：2026-06-04 | 类型：refactor (incremental, bash→z42)
> 模型：每个 increment 独立 — 加 native path → 本地验证匹配 .sh → 保留 .sh →
> CI-proven 后才 rewire CI + 删 .sh。

## Increment 1: bash-free primitives + `deps check` ✅
- [x] 1.1 xtask `_exec(Process)` — 直接 spawn 二进制（Process.WorkingDirectory），不经 bash
- [x] 1.2 xtask `_root()` (git 直接 spawn) + `_z42vm()` 解析继承运行时
- [x] 1.3 `deps check` native：compile check-versions-drift.z42（dotnet）→ 继承 z42vm 运行
- [x] 1.4 本地验证：`z42 xtask.zpkg deps check` exit 0 + 输出与 `.sh` 一致；0 次 bash spawn
- [x] 1.5 保留 check-versions-drift.sh；CI 不动；commit（xtask-only，零 CI 风险）

## Increment 2: `test changed` (outer native) ✅
- [x] 2.1 `_testChanged(argv)` — parse base/--dry-run，set env vars，经 driver `run` 跑 test-changed.z42（dotnet 直接 spawn，非 bash）
- [x] 2.2 本地验证：`test changed --dry-run` plan 与 `./scripts/test-changed.sh --dry-run` 一致，exit 0
- [x] 2.3 保留 test-changed.sh；CI 不动
- 注：test-changed.z42 内部仍 `bash -c "just …"` 跑映射命令 → 待 inner-bash 子 increment（映射改 `z42 xtask.zpkg …`）

## 后续 increments（每个一 commit，同结构）
- [ ] test vm / test cross-zpkg / build stdlib / test all（outer native）
- [ ] inner-bash 子 increment：各 .z42 内部 `just`/`bash -c` 映射 → native xtask 调用
- [ ] 各 increment CI-proven 后：rewire CI 调 `z42 xtask.zpkg …` + 删对应 .sh

## 备注
- 自托管边界保留：编译 .z42 需 dotnet（z42c）；build VM 需 cargo。xtask 直接 spawn。
- stdlib 就绪：Process.WorkingDirectory / Environment.SetCurrentDirectory 均已存在。
