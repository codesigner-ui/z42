# Tasks: add-desktop-platform-backend

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：infra + test（toolchain）

**变更说明：** desktop 作第 4 平台后端，补桌面 C-ABI facade 级 R1–R7 缺口，统一
`test platform` 管线。（原计划退役 host/examples 死副本 → 取消，见阶段 3：它是文档化示例非死物。）
**并行：** 占 toolchain；User 多次授权并行。设计见 proposal.md（User 2026-06-15 定 v1 全 R1–R7）。

## 阶段 1: C R1–R7 harness
- [x] 1.1 `platforms/desktop/tests/r1_r7.c`（z42_host.h；R1 smoke / R2 bad zbc=10 / R3 unknown entry=20 / R4 arg mismatch=21 / R5 resolver miss / R6 lifecycle / R7 multiline；每场景 `[Rn] PASS/FAIL`，全过 exit 0）
- [x] 1.2 `platforms/desktop/README.md`

## 阶段 2: DesktopBackend
- [x] 2.1 `xtask_test_desktop.z42` DesktopBackend：① libz42.a(cargo rustc staticlib) ② Assets(fixtures→desktop dir；stdlib 用 _libsDir 直指,不拷) ③ cc r1_r7.c + 链 + 跑 + 解析→junit
- [x] 2.2 注册：`xtask_test_platform.z42` `_platformDispatch`/`_platformAllPlatforms` 加 desktop
- [x] 2.3 `xtask.z42.toml` [sources] + `xtask_cli.z42` `_platformRouter` 加 desktop 叶子

## 阶段 3: 退役死副本 —— ⛔ 取消（删前发现非纯死物）
- [~] 3.1 ~~删 `src/toolchain/host/examples/`~~ → **不删**。删前细看：`host/README.md` 把 `hello_rust`(Tier-2 H2b 端到端，🟢)+ `hello_c`(C 参考) 当**文档化 host 示例**，platforms/README 2 处引用。DesktopBackend 是**新增** R1–R7 测试，不取代这些示例。按 philosophy（删前看清、矛盾即停）保留。examples/embedding vs host/examples 去重留专门 change 裁决（记 roadmap）。

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 build xtask 编译
- [x] 4.2 本地端到端：`./xtask test platform desktop` 7/7 + junit 产出
- [x] 4.3 cross-platform-testing.md 同步
- [x] 4.4 归档 + commit

## 备注
- libz42.a 的 native-static-libs（-liconv -lSystem 等）需从 cargo rustc 输出抓或硬编平台清单（参考旧 build.sh 的 native-static-libs.txt 抓法）
