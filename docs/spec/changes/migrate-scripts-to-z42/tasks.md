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

## Increment 3: native build family + dogfood foreach/String ✅
- [x] 3.1 `build runtime|compiler|launcher` → native `_exec` (cargo/dotnet 直接 spawn)
- [x] 3.2 `build feature-matrix` (interp-only/wasm/ios/android) native — replaces `just build-*-feature`
- [x] 3.3 `build stdlib` native — cold-start primer (raw z42c workspace + flat-view copy via foreach) + run build-stdlib.z42; warm path validated → 22 zpkgs
- [x] 3.4 dogfood：用 `foreach (var x in arr)` + `String.StartsWith/EndsWith`（均已实现），删 `_startsWith/_endsWith` workaround
- 注：foreach 与 String.StartsWith/EndsWith/Split 早已实现（之前误判为缺失）；workflow 审计确认真正 P0 gap 是 `File.GetLastWriteTime`(mtime, native extern) — 待 port 需要时补

## 后续 increments（每个一 commit，同结构）
- [ ] test vm / test cross-zpkg / test lib / test all（orchestration → native）
- [ ] bench run/diff / test-dist / package（platform packaging — 最大、最后）
- [ ] mtime native extern（File.GetLastWriteTime）当增量/freshness 检查需要时
- [ ] rewire CI（ci/bench-update/release）→ `z42vm xtask.zpkg -- …`；删 justfile + scripts/*.sh + _lib（留 install-z42.*）
- [ ] 更新 docs（workflow/、CLAUDE.md 等引用）

## 备注
- 自托管边界保留：编译 .z42 需 dotnet（z42c）；build VM 需 cargo。xtask 直接 spawn。
- stdlib 就绪：Process.WorkingDirectory / Environment.SetCurrentDirectory 均已存在。
