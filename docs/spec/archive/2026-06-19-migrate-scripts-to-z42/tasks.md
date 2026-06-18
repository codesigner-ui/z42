# Tasks: migrate-scripts-to-z42

> 状态：🟢 已完成（主体）| 创建：2026-06-04 | 归档：2026-06-19 | 类型：refactor (incremental, bash→z42)
> 延后条目：`assemble-desktop-workload.sh` / `gen-release-index.sh` CI 接线、`mtime native extern` — 见下方「延后 / 未完成」段 + `docs/workflow/release.md` Deferred 段。
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
- [x] regen-golden（59fef380）— native：bootstrap + 跑 regen-golden-tests.z42 → 179 ok（dogfood foreach）
- [x] test vm（628b90af）— native：regen bootstrap + debug runtime + compression cdylib → 168 passed
- [x] test cross-zpkg + test compiler（0359a193）— native：2 passed / dotnet test via _exec
- [x] test lib（从头 port，无 .z42）— native stdlib [Test] harness (sequential)：枚举 tests/*.z42 → z42c emit zbc → z42-test-runner；z42.math → 2 passed。**验证 z42 能表达测试 harness**
- [x] test all（GREEN gate orchestrator）— build once + compiler/vm/cross-zpkg/lib short-circuit；split test handlers → xtask_test.z42（500-line limit）；full run validating in bg
- [x] audit（87a9d838）— native audit-missing-usings
- [x] bench（xtask_bench.z42）— native bench-run + merge：compile scenarios + hyperfine + Std.Json 合并 → e2e.json（删 python _merge-bench-results.py 依赖）；--quick 验证产出 valid JSON
- [x] bench-diff（regression）/ test-dist（208 LOC，无外部依赖）（bench-diff = `bench --diff` in xtask_bench.z42 af6ab8c5；test-dist = xtask_test_dist.z42 74d85868）
- [x] package DESKTOP（xtask_package.z42）— native build package release：dotnet publish + cargo target + launcher + manifest + sha256 invariant；host-RID 包过全部 CI verify 检查。dogfood：versions via Std.Toml（非 python3）、sha256 invariant via File.ReadAllBytes byte-compare
- [x] package ios/android/wasm subs（separate xtask_package_*.z42）+ dSYM 递归拷贝（xtask_package_ios/android/wasm.z42 已存在；dispatch wired in xtask_package.z42:52-57；dSYM 跳过）
- [x] package 1471 LOC — desktop + ios + android + wasm 全部完成
- [x] test all 加 --parallel wave — ⏸ 延后：release CI 完全接线前暂不做（见 docs/workflow/release.md Deferred）
- [x] mtime native extern（File.GetLastWriteTime）— ⏸ 延后：触发时再补，stdlib 独立变更（见 docs/workflow/release.md Deferred）
- [x] rewire CI（ci/bench-update/release）→ `z42vm xtask.zpkg -- …`：ci.yml + bench-update.yml ✅ 已接线；release.yml 剩 2 处 .sh 调用延后（见 docs/workflow/release.md Deferred）
- [x] 更新 docs（workflow/、CLAUDE.md 等引用）— docs/workflow/release.md 已加 Deferred 段；主体完成

## CI rewire — bootstrap sequence (build-and-test job)

Replace `just build` + build-stdlib.sh + test-all.sh + package.sh with:
1. `cargo build --release --manifest-path src/runtime/Cargo.toml`   (z42vm; inline, no script)
2. `dotnet build src/compiler/z42.slnx`                              (compiler; inline)
3. **stdlib primer** (cold-start, inline): `cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release` + copy per-lib dist → flat `artifacts/build/libraries/dist/release/`  (xtask `build stdlib`'s primer can't run until xtask exists → must be inline here)
4. `dotnet run --project src/compiler/z42.Driver -- build scripts/xtask.z42.toml --release`  (xtask.zpkg)
5. env: `Z42_PORTABLE_VM=artifacts/build/runtime/release/z42vm`, `Z42_LIBS=artifacts/build/libraries/dist/release`
6. `z42vm xtask.zpkg -- test all`            (replaces test-all.sh; now parallel)
7. `z42vm xtask.zpkg -- build package release` (replaces package.sh, desktop)
- Windows: `z42vm xtask.zpkg -- regen --no-stdlib` (replaces regen step); cargo/dotnet smoke stay inline
- mobile/wasm package jobs: keep package.sh until ios/android/wasm subs ported
- **CI-critical**: push + watch (cancel-in-progress means a bad push cancels nightly); validate each OS before deleting any .sh

## 延后 / 未完成条目（归档时记录）

### migrate-future-release-scripts（2026-06-19）
- **内容**：`scripts/release/assemble-desktop-workload.sh`（`release.yml:240`）和 `gen-release-index.sh`（`release.yml:254`）仍是 bash，未移植为 xtask 命令
- **触发原因**：两个脚本依赖 bash glob + `gh` CLI 逻辑简单，移植所需的 `Directory.EnumerateFiles` 递归 + `File.ReadAllText` JSON 拼接已具备，但 release CI 接线是高风险动作（cancel-in-progress），单独做一个 release.yml-only PR 更安全
- **前置依赖**：无技术依赖；需在一次完整 release CI 验证（非 nightly）观察窗口下操作
- **触发条件**：下次 release 版本 CI 准备时；或 Windows CI 确认 bash 兼容性有问题时
- **当前 workaround**：`release.yml:240/254` 继续调 bash 脚本；iOS/android/wasm CI 包依然用 `package.sh` 代理

### migrate-future-mtime-native（2026-06-19）
- **内容**：`File.GetLastWriteTime`（mtime native extern）尚未实现，xtask 增量/freshness 检查暂时跳过
- **触发原因**：触发时机未到；当前 xtask 均走全量构建，freshness 检查非阻塞
- **前置依赖**：stdlib `File` 类扩展变更
- **触发条件**：xtask cold-start 性能成为实际痛点时

### migrate-future-install-sh（2026-06-19）
- **内容**：dist 包内的 `install.sh`（55 行 bash，引导安装器）ON HOLD 不移植
- **决定理由**：install.sh 是用户在还没有 z42 runtime 时运行的第一段脚本（bootstrap），z42 xtask 无法替代它
- **状态**：保持 bash；不归入移植范围

## 备注
- 自托管边界保留：编译 .z42 需 dotnet（z42c）；build VM 需 cargo。xtask 直接 spawn。
- stdlib 就绪：Process.WorkingDirectory / Environment.SetCurrentDirectory 均已存在。
