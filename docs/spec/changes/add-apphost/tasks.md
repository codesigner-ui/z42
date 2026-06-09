# Tasks: apphost —— 每-app 原生可执行文件

> 状态：🟡 规范就绪，**实施排队**（等 `port-z42c-core` 释放 toolchain 锁）| 创建：2026-06-09

## 进度概览
- [x] 阶段 0: 探索 + 叉路决策（产物形态=内嵌 patch / 来源=framework-dependent 本地优先 / 推进=先起草）
- [x] 阶段 1: proposal + design + spec 起草
- [ ] 阶段 6.5: gate 确认（重点 Decision 3 本地查询规则）
- [ ] 阶段 2(实施): 共享 lib + apphost stub（Rust）
- [ ] 阶段 3(实施): patcher 命令（z42）
- [ ] 阶段 4(实施): 测试 + 文档同步

> ⏸ **占锁前置**：阶段 2 起触代码 → 必须先确认 `toolchain` 锁空闲（`port-z42c-core` 已归档）→ 在 `ACTIVE.md` 持有表登记 `add-apphost` 占 `toolchain` → 再开工。

## 阶段 2: 共享 lib + apphost stub（Rust，toolchain）
- [ ] 2.1 `src/toolchain/launcher/src/lib.rs`：`Runtime` 结构 + `z42_home()` + `probe_runtime(dir)`（installed/portable 两式判定）+ `exec_core(rt, core_args)`
- [ ] 2.2 `src/toolchain/launcher/src/main.rs`：trampoline 改调 lib，删内联 `resolve_runtime`，行为不变（installed → portable）
- [ ] 2.3 `src/toolchain/launcher/src/bin/apphost.rs`：占位符 static（MAGIC + payload）+ `parse_target()` + 本地优先解析（`$Z42_HOME` → exe 上行 `.z42` → `$HOME/.z42`）+ 注入 `<app.zpkg> --` + exec + 设 `Z42_LIBS`
- [ ] 2.4 `src/toolchain/launcher/Cargo.toml`：加 `[lib]` + apphost `[[bin]]`
- [ ] 2.5 Rust 单测：占位符 patch/未 patch；解析顺序三态（Z42_HOME > 本地 > 系统）+ 无运行时

## 阶段 3: patcher 命令（z42，toolchain）
- [ ] 3.1 `src/toolchain/launcher/core/apphost.z42`：`apphost build <app.zpkg> [--out]`——定位模板（z42vm 同级）→ 搜 MAGIC → 覆写 payload（路径+NUL，越界报错）→ 写出 + chmod 0755
- [ ] 3.2 `src/toolchain/launcher/core/launcher.z42`：命令分发加 `apphost` → 委派 apphost.z42
- [ ] 3.3 `src/toolchain/launcher/core/z42.launcher.z42.toml`：纳入 `apphost.z42`
- [ ] 3.4 确认 `Std.IO` 有 set-permission / chmod；缺则按 dogfill 在 z42 stdlib 补（停下汇报，记 `project_scripts_z42_port` 缺口）
- [ ] 3.5 `src/toolchain/launcher/core/tests/apphost_patch_test.z42`：patch 字节校验 + 未配置模板报错

## 阶段 4: 测试 + 文档同步
- [ ] 4.1 e2e 烟测：patch 样例 app → `./app foo` → 断言 app 见 `[foo]` + 退出码透传（挂 dist smoke）
- [ ] 4.2 `src/toolchain/launcher/README.md`：文档新 lib / apphost bin / `apphost build` 命令
- [ ] 4.3 `docs/design/runtime/launcher.md`：新增 "apphost" 段（机制 + 本地优先解析规则 + Deferred 5 项）
- [ ] 4.4 `docs/roadmap.md`：Deferred Backlog Index 加 5 条索引行
- [ ] 4.5 GREEN：`z42 xtask.zpkg test`（+ 发行相关 `test dist`）
- [ ] 4.6 spec scenarios 逐条覆盖确认
- [ ] 4.7 归档：移 `docs/spec/archive/YYYY-MM-DD-add-apphost/` + 释放 toolchain 锁 + commit/push

## 备注
- 实施前每文件对照 proposal Scope；越界立即停回阶段 3。
- Decision 3（本地查询规则）gate 处确认；若 User 要"也含 cwd 上行"，回 design + spec 增一条 scenario 再开工。
- launcher.z42 已 498 行（逼近软上限）→ patcher 必须落新文件 apphost.z42，不加塞 launcher.z42。
