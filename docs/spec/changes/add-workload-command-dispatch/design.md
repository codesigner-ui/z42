# Design: workload 命令分发 B1+B2

## Architecture

```
启动时（launcher_cli.z42 Main 入口）：
  1. 注册 core commands（build / run / install / workload / ...）← 现有
  2. WorkloadCommandDiscovery.Discover(root)                   ← B1 新增
       scan $Z42_HOME/runtimes/<ver>/workloads/*/commands/*.cmd.toml
       → for each .cmd.toml: root.AddSpawn(verb, desc, zpkgPath)

运行时（用户敲 `z42 greet`）：
  3. Std.Cli 路由到 spawn-leaf 节点
  4. launcher 调 _spawnCommand(zpkgPath, ["greet", ...args])
       → Process: z42vm <zpkgPath> -- <args>  （复用 launcher._runWithVm）
  5. exit code 透传
```

## Std.Cli 扩展：AddSpawn

```z42
// SubcommandRouter.AddSpawn — 注册 spawn 式叶子命令
// 被触发时 runner.Spawn(zpkgPath, remainingArgv) 被调用
// help 文本直接用 desc（不需要 ArgParser）
public void AddSpawn(string verb, string desc, string zpkgPath);
```

内部实现选项：
- **选项 A**：`SpawnLeaf` 作为新的 `ICommandEntry` 实现，存储 zpkgPath；
  router 的 dispatch 逻辑对 `SpawnLeaf` 调 `runner.Spawn(...)` 而非 `parser.Run(...)`
- **选项 B**：把 spawn 包成一个 `ArgParser`（`AddOption("*")` 捕获全部剩余 args）
  并在 handler 里 spawn。缺点：`--help` 被 ArgParser 拦截而非透传给 zpkg

**决定选项 A**：spawn-leaf 语义干净，help 透传到 zpkg 自然，也避免 ArgParser 过度捕获参数。

## WorkloadCommandDiscovery

```z42
// launcher_workload_commands.z42
class WorkloadCommandDiscovery {
    // 扫描当前激活 runtime 下的所有 workload 命令目录
    // 按 workload 名字母序 sort，保证确定性（防 first-wins 不确定）
    public static void Discover(SubcommandRouter root, string runtimeDir);
}
```

扫描路径：`<runtimeDir>/workloads/*/commands/*.cmd.toml`

注意点：
- **必须按 workload 名字母序 sort**（common-pitfalls.md §1）：`Directory.GetDirectories`
  跨 OS 顺序不定，first-wins 注册前显式 `.OrderBy(StringComparer.Ordinal)`
- 同 verb 后者跳过（first-wins-by-sorted-name）
- `.cmd.toml` 解析失败 → 警告 + 跳过（不 crash launcher）
- 命令 zpkg 路径用绝对路径（相对于 .cmd.toml 所在目录）

## .cmd.toml 格式

```toml
verb = "greet"                           # 命令动词（z42 greet）
description = "Say hello (greet wl)"    # --help 说明文字
zpkg = "greet.zpkg"                     # 相对 .cmd.toml 的 zpkg 路径
min_runtime = "0.3.14"                  # 可选：最低运行时版本
```

解析使用现有 `Std.Toml`（或轻量自行实现，视 Std.Toml 覆盖能力决定）。

## workload install 集成

`z42 workload install <wl>` 现有逻辑：解包 runtime tgz → `workloads/<wl>/runtime/`

B2 扩展：若包含 `[commands]` 段（workload.toml），额外解包命令 zpkg 到
`workloads/<wl>/commands/`。

```toml
# workload.toml（扩展 [commands] 段）
[commands]
entries = [
  { cmd = "greet.cmd.toml", zpkg = "greet.zpkg" }
]
```

**B2 minimal 策略**（降低 scope）：
不修改现有 install 逻辑，改为：
- 命令 workload 用 `z42 workload install --from <dir>` 本地安装（已有）
- 安装脚本直接把 .cmd.toml + .zpkg drop 到 `commands/` 目录
- B2 全量（workload.toml `[commands]` 段 + auto-extract）留延后

## Decisions

### D1: AddSpawn 加在哪个 router 层级

**问题**：`z42 publish android` 是两层（publish → android），`z42 greet` 是一层。
外部命令可以在任意层级注册吗？

**决定**：**只在 root 层发现+注册**（`z42 <verb>`）。
理由：workload 命令 verb 直接注册到 root 最清晰；多层命令（`z42 publish android`）
属于 SDK 内置（launcher_export.z42），不走 workload 发现路径。后续需要时可扩展
.cmd.toml 的 `parent = "publish"` 字段，但当前不实现。

### D2: Spawn 时的 argv 构造

launcher 调用方式：
```
z42vm <zpkgPath> -- greet [user args...]
```
第一个参数传 verb 名，zpkg 可以用它做路由（若 zpkg 承载多个命令）。
等同现有 `z42 run <app.zpkg>` 的 argv 透传逻辑。

### D3: 是否实现 `z42 <cmd> --help` 透传

**决定**：是。spawn-leaf 不拦截任何参数，`--help` 原样透传给 zpkg；zpkg 自带
Std.Cli 的话会自动处理。`z42 --help` 只展示 description，不展示子命令 help。

### D4: B2 minimal vs 完整

完整 B2 = workload.toml `[commands]` 段 + install 时自动 extract。
Minimal B2 = .cmd.toml + .zpkg 手动放到 commands/ 目录（`--from <dir>` 本地装）。

**决定**：先实现 Minimal B2（工作量小，B1 可验证），完整 B2 留延后。
理由：0.3.14 目标是验证 B1 command discovery 机制工作，不是完整的包格式。

## Testing Strategy

- `examples/workloads/greet/greet.z42` — 一个极简 `z42 greet` 命令（打印 "Hello, world!"）
- `examples/workloads/greet/greet.cmd.toml` — 对应的 cmd.toml
- 测试步骤：
  1. `z42c build examples/workloads/greet/greet.z42.toml` → greet.zpkg
  2. 手动 cp greet.zpkg + greet.cmd.toml 到 `$Z42_HOME/runtimes/<ver>/workloads/greet/commands/`
  3. `z42 --help` → 显示 "greet: Say hello (greet wl)"
  4. `z42 greet` → 输出 "Hello, world!"
- SubcommandRouter.AddSpawn 单元测试（sort 顺序 + first-wins）

## Deferred

### add-workload-command-dispatch-future-auto-download
- **触发原因**：本次只做本地 install；网络按需下载需 GitHub Releases 格式定稿
- **前置依赖**：workload.toml `[commands]` 段 + manifest `workloads` 段 + 网络 install
- **触发条件**：0.4.x 工具链规划时

### add-workload-command-dispatch-future-nested-verbs
- **触发原因**：`z42 publish android` 这类多层命令当前不走 workload 发现
- **前置依赖**：.cmd.toml 加 `parent` 字段 + router 递归注册
- **触发条件**：有具体多层命令需求时

### add-workload-command-dispatch-future-inprocess
- **触发原因**：subprocess spawn 每次有进程启动开销；in-process 需 `__load_zpkg`
- **前置依赖**：runtime-dynamic-load-call VM builtin 实现
- **触发条件**：bench 显示 spawn 开销不可接受时
