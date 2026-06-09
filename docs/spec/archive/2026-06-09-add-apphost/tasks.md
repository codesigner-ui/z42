# Tasks: apphost —— 每-app 原生可执行文件

> 状态：🟢 已完成并归档（feat a3720a16，2026-06-09）| 创建：2026-06-09 | toolchain 锁：User 授权与 port-z42c-core 并行，归档已释放

## 进度概览
- [x] 阶段 0–1: 探索 + 叉路决策 + proposal/design/spec 起草（gate 已过：D3 本地规则 = exe 上行 + 本地优先；D6 z42.toml apphost 布尔）
- [x] 阶段 2(实施): 共享 lib + apphost stub（Rust）+ 12 单测
- [x] 阶段 3(实施): patcher 命令（z42）+ z42.encoding 依赖 + **macOS ad-hoc 重签名（D7）**
- [x] 阶段 4(实施): 打包折进 P1（D8）+ dist smoke + 文档同步
- [x] e2e 验证：手动全链路 + dist apphost smoke 全过

## 实施期关键发现（已记入 design Decision 7 + launcher.md）
- **D7 macOS 代码签名**：patch 二进制使签名失效 → 内核拒绝运行（hang）。patcher 在 macOS `codesign -s - -f` 重签名（.NET 同款）。
- **占位符必须 volatile 读**：release（LTO/opt-z）会把"读 const 初始化的 static"const-fold 成编译期值 → patch 无效（debug 巧合可用）。修复：`core::ptr::read_volatile` 读占位符。**这正是 dist smoke 抓出来的——单测抓不到（不经 static 路径）。**
- **D8 打包折进 P1**（User 裁决）：原延后的"包内置模板"折进——`build package` 铺 `bin/apphost`、`install.sh` 装、dist smoke 覆盖。

## 阶段 2: 共享 lib + apphost stub（Rust，toolchain）
- [ ] 2.1 `src/toolchain/launcher/src/lib.rs`：`Runtime` 结构 + `z42_home()` + `probe_runtime(dir)`（installed/portable 两式判定）+ `exec_core(rt, core_args)`
- [ ] 2.2 `src/toolchain/launcher/src/main.rs`：trampoline 改调 lib，删内联 `resolve_runtime`，行为不变（installed → portable）
- [ ] 2.3 `src/toolchain/launcher/src/apphost.rs`：占位符 static（MAGIC + payload）+ `parse_target()` + 本地优先解析（`$Z42_HOME` → exe 上行 `.z42` → `$HOME/.z42`）+ 注入 `<app.zpkg> --` + exec + 设 `Z42_LIBS`
- [ ] 2.4 `src/toolchain/launcher/Cargo.toml`：加 `[lib]` + apphost `[[bin]]`
- [ ] 2.5 Rust 单测：占位符 patch/未 patch；解析顺序三态（Z42_HOME > 本地 > 系统）+ 无运行时

## 阶段 3: patcher 命令（z42，toolchain）
- [ ] 3.1 `src/toolchain/launcher/core/apphost.z42`：`apphost build <app.zpkg> [--out]`——定位模板（z42vm 同级）→ 搜 MAGIC → 覆写 payload（路径+NUL，越界报错）→ 写出 + chmod 0755
- [x] 3.2 `launcher.z42`：命令分发加 `apphost` + help
- [x] 3.3 `z42.launcher.z42.toml`：加 `z42.encoding` 依赖（`*.z42` glob 自动纳入 apphost.z42）
- [x] 3.4 `Std.IO` 已有 `File.MakeExecutable` + `File.SymLink`（无 dogfill 缺口）
- [x] 3.5 `PatchBytes` 纯函数（dist smoke 覆盖；core/tests [Test] 因 launcher core 无 test-runner 接线作废）

## 阶段 4: 打包 + 测试 + 文档
- [x] 4.0 D8 打包：`xtask_package.z42` 铺 `bin/apphost` + `install.sh` 装 `launcher/apphost`
- [x] 4.1 dist smoke `_apphostSmoke`（`xtask_test_dist.z42`）：build app → apphost build → 跑产出 exe → 断言 `APPHOST_OK` ✅ 通过
- [x] 4.2 `src/toolchain/launcher/README.md`
- [x] 4.3 `docs/design/runtime/launcher.md` apphost 段
- [x] 4.4 `docs/roadmap.md` Deferred Backlog Index
- [x] 4.5 GREEN：12 Rust 单测 + launcher core/xtask 编译 + dist apphost smoke + launcher smoke 全过
- [x] 4.6 spec scenarios 覆盖确认
- [x] 4.7 commit/push（feat a3720a16）+ 归档 archive/2026-06-09-add-apphost/ + 释放 toolchain 并行占用

## 备注
- **pre-existing 失败（非本变更）**：`z42 xtask.zpkg test dist` 的 `secp256k1` golden FAIL —— 该用例是**多文件**（source.z42 + vectors.z42 共享 namespace），dist runner 只单文件编译 source.z42 → 缺 vectors.z42 符号。**与 apphost 无关**（本变更 0 改 crypto/dist-enumeration），属 dist-harness 多文件局限，单独 issue 跟踪。
- GREEN 范围：本变更 toolchain-isolated；未重跑 vm/cross-zpkg/lib stage（不触 runtime/compiler/stdlib，行为不受影响）。trampoline 重构经 launcher portable+installed smoke 验证行为不变。
- D7 codesign + volatile 读：见进度概览"实施期关键发现"。
