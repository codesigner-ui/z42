# impl-command-discovery — design（B1）

## 机制（launcher-command-dispatch.md 落地）

```
launcher 启动：
  1. 代码注册 core            → router.Add("run", ...)（已有）
  2. 扫命令目录 + 读 manifest  → 对每个发现的命令 router.Add(name, desc, spawn-leaf)
  3. root.Resolve(argv)：
       命中 core handler → 直接执行（_dispatchLauncher）
       命中发现命令      → spawn `z42vm <cmd.zpkg> -- <剩余 argv>`（同 _cmdRun）
```

## Decisions（需 User 拍）

### Decision 1：鸡蛋问题 — 用什么作证
**问题**：发现机制做出来，现在没有可发现的命令（build=z42c；new/test/fmt 非 launcher 命令；export/publish 现 baked）。
**选项**：
- **X 迁 export/publish 成发现式**：把 launcher_export.z42 + 依赖**抽成独立命令 zpkg**（`z42-export.zpkg`），launcher 改 spawn 它。立即用上机制 + 减 launcher 体积。**代价大**：要拆 build/打包，export 逻辑脱离 launcher.zpkg 单独编 + 随 SDK 发布。
- **Y 机制 + test-only 作证**：只做发现机制 + 一个最小测试命令 zpkg 证明 discover+dispatch 通。real 命令迁移留后续。**轻**，但 B1 本身无 user-facing 价值（纯 infra）。
- **Z 重排：先 B2 再 B1**：B2 产出含命令的 workload，B1 顺势发现它们——infra + content + 真实 proof 一次到位。但偏离"按 B1 顺序"。

**推荐**：**Z（重排 B2→B1，或合做）**。理由：B1 单独是"无内容的发现器"，价值要等有命令可发现才兑现；B2 的 workload 恰好就是那批命令的来源。先 B2 产出 ios/android workload（含平台命令 zpkg），B1 再发现它们——既给 infra 又立即有真实命令验证，避免建一套发现机制空转。次选 Y（轻量证机制，real 迁移延后）。X 代价最大、收益最先但工程量超 B1 预期。

### Decision 2：命令目录布局 + manifest
**推荐**：
- SDK（版本作用域）：`$Z42_HOME/runtimes/<ver>/commands/<name>.zpkg` + 同名 `<name>.cmd.toml`。
- Workload：`$Z42_HOME/runtimes/<ver>/workloads/<wl>/commands/<name>.zpkg` + `<name>.cmd.toml`。
- `<name>.cmd.toml`：`[command] name= / description= / usage=`（供 `z42 -h` 一行描述 + 叶子 help）。
**理由**：扁平 `<name>.zpkg`+sidecar manifest 最简；版本/workload 作用域沿用现有 `runtimes/<ver>` 布局。

### Decision 3：SubcommandRouter 怎么接发现命令
**问题**：发现命令是"透传 spawn"叶子，不像 baked 命令有 ArgParser + 代码 handler。
**选项**：
- A 在 dispatch 层特判：发现命令名记一张表，`_runLauncher` 解析前先查表 → 命中即 spawn（类似现 `run` 透传）。Std.Cli 不改，但 `z42 -h` 不自动列发现命令。
- B 扩 Std.Cli：SubcommandRouter 加"spawn 式叶子"（AllowExtras + 标记 external + 内嵌 zpkg 路径），`-h` 自动列、dispatch 统一。
**推荐**：**B**（launcher-command-dispatch.md 的核心卖点就是"发现命令与 baked 同树、`z42 -h` 统一列出带描述"；A 退回 kubectl 式"父 help 给不出外部命令描述"的老问题）。代价：动 z42.cli（stdlib），跨 stdlib 锁。

### Decision 4：优先级 / 保留名
core baked 命令优先；发现命令不得用保留名（run/install/list/...）——冲突时 core 赢 + warn。

## Implementation Notes
- dispatch spawn 复用 `_cmdRun` 的 vm 解析 + `Z42_LIBS` 设置 + stdio 继承 + 退出码回传。
- 扫描循环按 common-pitfalls §1 **显式 sort**（命令注册顺序确定性）。

## Testing Strategy
- z42.cli 新增 spawn-leaf 的 [Test]（若选 Decision 3-B）。
- e2e：放一个发现命令 zpkg → `z42 <cmd> -- args` spawn 通 + `z42 -h` 列出。
