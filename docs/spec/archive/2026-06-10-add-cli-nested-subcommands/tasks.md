# Tasks: Std.Cli 嵌套子命令

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统：`stdlib`（add-reflection-type-flags 已归档释放后占用）

## 进度概览
- [x] 阶段 1: 嵌套存储 + AddRouter
- [x] 阶段 2: Resolve + CommandResolution（核心）
- [x] 阶段 3: 测试与文档

## 阶段 1: 嵌套存储
- [x] 1.1 `SubcommandRouter.z42`：加 `_childRouters: SubcommandRouter[]` + `_isRouter: bool[]` parallel array，构造函数初始化，`_grow` 同步扩容
- [x] 1.2 `SubcommandRouter.z42`：实现 `AddRouter(name, desc, child)`（同名覆盖语义对齐 `Add`）；`Add` 标记 `_isRouter=false`
- [x] 1.3 `Has(name)` 覆盖叶子 + 子 router（已遍历 `_names`，无需改）

## 阶段 2: 核心实现
- [x] 2.1 新增 `CommandResolution` 类（三态标志 + `Path`/`Result`/`HelpText`/`ErrorMessage`；静态工厂 Match/Help/Unknown 保证互斥）→ 拆到独立 `src/CommandResolution.z42`
- [x] 2.2 `Resolve(argv)`：递归下钻 + 回溯 `Prepend` 拼 path，按 design Decision 4 的判定顺序实现 help/unknown/下钻/叶子收尾
- [x] 2.3 叶子 `ArgParser.Parse` 的 `ShowHelp()` → 归 `IsHelp` 并取叶子 `HelpText()`
- [x] 2.4 `Match`/`SubcommandMatch` 原样保留（不改）；`Resolve` 与之独立
- [x] 2.5 行数自检：`SubcommandRouter.z42` 拆分后回 ~260 行；`CommandResolution` 移入独立文件（已回阶段 3 更新 Scope）

## 阶段 3: 测试与验证
- [x] 3.1 `tests/cli_nested_subcommand.z42`（NEW [Test]，14 项）：2 层/3 层命中、顶层/中间层/叶子层 help、顶层/中间层 unknown、三态互斥、叶子 CliException 透传 —— 逐条对应 spec scenario
- [x] 3.2 现有 `cli_subcommand.z42` 回归仍绿（单层 Match 零破坏，13/13）
- [x] 3.3 `src/libraries/z42.cli/README.md`：嵌套用法 + 入口示例 + 支持形式表更新（顺带修正已全部 ship 的 stale Deferred 列表）
- [x] 3.4 `docs/design/stdlib/cli.md`：特性决策表 row 1（Subcommand）"不支持"→"嵌套已落地"；新增 `cli-future-nested-subcommand` ✅ 小节（API + 三态语义）
- [x] 3.5 `z42.cli` lib test 全绿（9 文件 / 含 14 新嵌套 [Test]）
- [x] 3.6 完整 GREEN gate：`xtask test` 全绿 —— compiler + vm(interp+jit) + cross-zpkg + stdlib(269 文件/22 lib)（经 fresh 0.14 vm 旁路；apphost bundled vm 陈旧待 ② 重建）
- [x] 3.7 golden `.zbc`：测试构建自动产 `tests/dist/*.zbc`（无需独立 regen）
- [x] 3.8 spec scenarios 逐条覆盖确认（16 场景 ↔ 14 [Test] + cli_subcommand 回归）

## 备注（实施期发现）
- **bootstrap 编译器限制**：在 `SubcommandRouter` 自身方法内对**同类型另一实例（数组元素）**直接 `.Resolve(...)` 触发 `E0402 has no method`（对别的类型 `ArgParser.Parse` 无此问题）。绕法：先赋值到带类型局部变量 `SubcommandRouter child = this._childRouters[i];` 再调用。已在 SubcommandRouter.Resolve 落地。
- **环境**：实施期 worktree 处于 add-reflection-type-flags 的 0.14 bump 落地点；repo-root `./xtask` apphost 的 bundled launcher z42vm 仍陈旧 0.13，无法读 0.14 zpkg。验证经 `artifacts/build/runtime/release/z42vm`（0.14）旁路完成。apphost 重建归 ② / 独立 toolchain 维护，不属本 stdlib 变更。

## 备注
- 本变更是 ② 的前置依赖（xtask/launcher 迁移 + 命令树重组，合流 migrate-scripts-to-z42，toolchain 子系统）。② 在本变更归档后启动。
- 排期：stdlib 锁现被 add-reflection-type-flags 占用，本变更规范先定稿，实施排队等其归档。
