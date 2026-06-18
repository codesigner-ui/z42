# workload-subsystem-future — 整合归档

> 状态：**🟢 已归档 2026-06-19（全部延后，未实施）**
> 来源：合并 `build-workload-subsystem`（B1–B5 program charter）+ `impl-command-discovery`（B1 细化）
> 归档原因：B1–B5 全部明确延后——前置条件为反射 MVP + 编译器自举 + `runtime-dynamic-load-call` VM 实现完成。
> 延后条目已同步入 `docs/design/toolchain/launcher-command-dispatch.md` Deferred 段 + `docs/roadmap.md`。

---

## 结论（归档时）

| Phase | 内容 | 状态 | 前置条件 |
|-------|------|------|---------|
| **B1** 命令发现机制 | launcher 扫命令目录 + manifest → 注册进 Std.Cli 树 | ⏸ 延后 | B2（需有可发现的命令）|
| **B2** workload 包格式 + `z42 workload install` | manifest `workloads` 段 + 版本作用域安装 | ⏸ 延后 | runtime-dynamic-load-call VM 实现 |
| **B4** 测试改 workload 驱动 | R1–R7 平台一致性测试 = workload 生成/驱动 | ⏸ 延后 | B3（已完成）+ B2 |
| **B5** 导出/发布完整生命周期 | `z42 new/platform add/export/publish/test` | ⏸ 延后 | B1–B4 |

> **B3（add-desktop-export）已完成** ✅ 77a5acba：`[platform.desktop]` + `z42 export/publish desktop` 产 apphost。

**B1 鸡蛋问题决策（impl-command-discovery Decision 1）**：选方案 Z——先 B2 产出含命令的 workload，B1 再发现它们。B1 单独做 = 发现 0 个命令，无 user-facing 价值。实施顺序应为 **B2 → B1 → B4 → B5**。

---

## B1 设计摘要（来自 impl-command-discovery）

### 机制
```
launcher 启动：
  1. 代码注册 core            → router.Add("run", ...)（已有）
  2. 扫命令目录 + 读 manifest  → 对每个发现的命令 router.Add(name, desc, spawn-leaf)
  3. root.Resolve(argv)：
       命中 core handler → 直接执行（_dispatchLauncher）
       命中发现命令      → spawn `z42vm <cmd.zpkg> -- <剩余 argv>`（同 _cmdRun）
```

### 关键决策（未拍，归档时记录推荐）
- **D1 鸡蛋问题**：方案 Z（重排 B2→B1），见上。
- **D2 命令目录布局**：`$Z42_HOME/runtimes/<ver>/commands/<name>.zpkg` + `<name>.cmd.toml`；workload 命令在 `runtimes/<ver>/workloads/<wl>/commands/`。
- **D3 Std.Cli 接入**：推荐 B（扩 SubcommandRouter 加"spawn 式叶子"），让 `z42 -h` 统一列出发现命令带描述；A（特判表）退回"无描述"问题。
- **D4 保留名优先**：core baked 命令优先；发现命令不得用保留名。

### 实施要点（备查）
- 扫描循环按 `common-pitfalls §1` 显式 sort（命令注册顺序确定性）。
- dispatch spawn 复用 `_cmdRun` vm 解析 + `Z42_LIBS` + stdio 继承 + 退出码回传。
- Std.Cli 扩展需跨 `stdlib` 锁。

---

## B2–B5 设计来源

- [launcher-command-dispatch.md](../../../design/toolchain/launcher-command-dispatch.md) — Core/SDK/Workload 三层 + 目录发现
- [runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md) — workload 安装 / manifest / 打包
- [platform-export-lifecycle.md](../../../design/toolchain/platform-export-lifecycle.md) — `z42 new/export/publish/test`、managed+eject
