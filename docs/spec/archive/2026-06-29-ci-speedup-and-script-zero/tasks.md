# Tasks: CI 提速 + script-zero（自驱 /loop 会话）

> 状态：🟢 已完成 | 归档：2026-06-29
> 类型：ci / refactor / perf / fix（轻量直做，非 lang/ir/vm 规范变更）
> 目标：缩短 CI 墙钟 + 把 scripts/ shell 收敛到 xtask（script-zero）。

## 成果概览

**CI 关键路径 ~47min → ~36min**（`compile-toolchain ~13min → package-host(windows) ~17min → publish-nightly ~3min`）。剩余为自举语言工具链的**固有成本**（z42c+stdlib 自举编译 + 多平台打包组装），已逼近"CI 配置可砍"的下限。

## 已落地（均已推送 + CI 绿）

### A. 缓存根因修复（最大结构性胜利）
- [x] `actions/cache` 缓存错目录（`src/runtime/target` vs 真实 `artifacts/build/runtime`，由 `.cargo/config.toml` target-dir 决定）→ **cargo 编译缓存从未命中**。换 `Swatinem/rust-cache@v2` + `workspaces: src/runtime -> ../../artifacts/build/runtime` + key bump v2 逃离污染缓存。cranelift（~12 crate，全栈最重）等现跨 run 缓存。
- [x] release.yml 同样错路径 → 同步迁 Swatinem。
- [x] **host-package 独立缓存键 `package-host-v2`**：之前与 compile-toolchain 共享 host-v2（只 bin/rlib），导致 package 的 cdylib/staticlib 每轮冷编 ~6-8min；独立键后暖缓存命中（实测 `Compiling z42` 2→0）。

### B. 关键路径 / 拓扑
- [x] `current-sdk` 组装（golden regen ~9min）拆出 `compile-toolchain` → 独立 `compile-test-assets` job，不再卡 `package-*→publish-nightly` 关键路径（package 提前 ~10min 启动）。
- [x] golden regen 串行 → 并行批 8（本地 692%CPU/6.9x；zbc-format 基线字节一致）。
- [x] A1 `test --no-build`：build-and-test / vm-jit / stdlib-jit 跳过二次重建。
- [x] **#5 host package 去 `--target`**：host RID 复用缓存的无-target 构建（`.cargo/config` 无 target-specific rustflags → 字节等价）；本地 dogfood 验证包完整 + z42vm 可跑。
- [x] CI job 命名统一 `<动作>-<目标>[-<scope>](<host-arch>)`：compile-toolchain / compile-test-assets / test-host / package-host（「编译测试资产」阶段现可见）。

### C. script-zero（scripts/ 仅剩 install-z42.sh）
- [x] `ci-stage-toolchain.sh` → `xtask build stage-toolchain`（字节一致 dogfood 验证）。
- [x] `ci-bootstrap.sh` → composite action `.github/actions/ci-bootstrap`（10 处调用统一 `uses:`；cold-start primer 无法纯 xtask，composite action 是正解）。
- [x] `generate-fixtures.sh` ×2（zbc/zpkg-format）→ 删（dead：调已移除的 dotnet；zbc 由 `xtask regen` 维护，zpkg regen 命令记 TODO）。
- [x] **`selfhost-bootstrap.sh` → 删**：其"从种子重建 + gen1==gen2 不动点"逻辑**本就在 xtask**（`_buildCompilerZ42` + `_testZ42cSelfHostByteIdentical`）。verify-selfhost 改用 `no-dotnet 守卫(→$GITHUB_PATH) + ci-bootstrap action + xtask test compiler`。workflow_dispatch 验证：不动点 7/7 gen1==gen2 + [Test] 17 units + e2e，no-dotnet 守卫不误触。

### D. 其它
- [x] 并发组按 `event_name` 解耦（push/schedule 不再互杀，消除假红+省重复轮）+ **publish-nightly 加 job 级串行化**（防解耦放开的 delete→create 竞态）。
- [x] 历史措辞清理：CI job 显示名 + 注释里 "C#-free / remove-dotnet / no dotnet" 等迁移史措辞（保留功能性 dotnet 守卫）。
- [x] Rust 依赖审计：删 criterion `html_reports`（plotters 子树，-85 行 lock）；cranelift/libffi/jsonschema 均为必需或 dev-only，无大块死重量可删。

## 事故 + 恢复（完整闭环）
- [x] 并发组解耦暴露 publish-nightly 的 `gh release delete→create` 竞态 → orphan run 删了 nightly release 没重建 → **CI 死锁**。
- [x] 修复（防复发）：publish-nightly job 级串行化。
- [x] 恢复（经 User 授权）：本地组装最小种子包（z42c+stdlib zpkg，平台无关，4 RID，isDraft=false）上传重建 release → workflow_dispatch → bootstrap 成功 → 自愈重发完整 nightly（19 资产）。
- [x] 成因+恢复步骤入 memory：`reference_nightly_deleted_deadlock_recovery`。

## Deferred / 待 User 动作

### D-1: 分支保护 required status checks（待 User，非阻塞）
- **现状**：`main` 分支**当前无任何分支保护**（`GET .../branches/main/protection` → 404）。所以 job 改名**没破坏任何合并门**——之前"必须改分支保护"的提醒基于错误假设，已校正。
- **若 User 想加保护**（require CI 绿才能合并）：分支 `main`，Settings → Branches → Require status checks，用**新 job 显示名**作 required check，建议核心门：
  `test-host(linux-x64)` / `test-host(linux-arm64)` / `test-host(macos-arm64)` / `test-host(windows-x64)` / `test-compiler-stdlib(linux-x64)` / `verify-features(linux-x64)`。
  注意：matrix shard（`test-vm-jit(...) shard N`）一般不单独 require；`publish-nightly` 是合并后发布，**不要** require。
- **触发条件**：User 决定要不要加仓库治理级分支保护。Claude 无权改仓库设置，按 User 选的清单代设即可。

### D-2: package 流水线并行化（唯一剩下的墙钟杠杆，独立大工程）
- package-host 的 15min 大头是**非 cargo**：z42c 编 launcher.zpkg + 跨平台 workload 组装 + SDK/runtime 打包 + SHA + Windows 慢文件操作。缓存碰不到。
- 再降需把 package 步的独立构建/组装并行化，或让 z42c 编译提速——独立 change，**勿与别的混**（release-critical）。

### D-3: zpkg-format regen 命令（z42-native，记于 `src/tests/zpkg-format/README.md`）
### D-4: `.claude/rules/version-bumping.md` 全量重写（dotnet staleness，已加 banner 标记）

## 备注
- 本会话工作横跨 `compile-once-toolchain` change 的「CI 去冗余」维度；该 change 余下 scope（成对分代 / 三发布门 / cross-bootstrap）续开，不在本需求内。
- 全部改动均独立 commit + 推送 + CI 绿验证（cold 路径以 workflow_dispatch 验证）。
- 归档后做了一轮"问题扫描"并清零 2 个遗留：① `gc_cycle_bench` 缺 `class_flags` 字段（预存 rot，CI 不编故静默腐烂；补字段，本地 `cargo build --benches` 全过，commit 17be4fbc）；② `testing/bootstrap.md` §6 stale（写 selfhost-bootstrap "待改造"，实已删；改为当前状态，commit e6dd69ad）。无其它真问题。
