# Design: Std.Cli 嵌套子命令

## Architecture

命令树是 router 节点 + 叶子 ArgParser 的混合树：

```
SubcommandRouter "xtask"
├── (leaf)   audit       → ArgParser
├── (leaf)   package     → ArgParser              ← package 提顶层后（②做，本变更不动 xtask）
├── (router) build       → SubcommandRouter "xtask build"
│             ├── (leaf) runtime  → ArgParser
│             ├── (leaf) package  → ArgParser      ← 迁移前的旧位置（②会删）
│             └── ...
└── (router) test        → SubcommandRouter "xtask test"
              ├── (leaf) vm  → ArgParser
              └── ...
```

`Resolve(argv)` 从根 router 出发，逐 token 下钻：router → 继续递归，叶子 → 交 `ArgParser.Parse` 收尾。三种结局封装进 `CommandResolution`。

## Decisions

### Decision 1: 新增 `Resolve`/`CommandResolution`，保留 `Match`/`SubcommandMatch`

**问题：** 现有 `Match()` 返回 `SubcommandMatch | null`（null = 顶层 help）。这个二态契约无法表达"哪一层的 help / 哪一层 unknown"，嵌套必须更丰富的结果。

**选项：**
- A — 改造 `Match` 返回值，复用 `SubcommandMatch`（加 `IsHelp`/`Path`）。优：单入口；缺：破坏现有 null-return 契约（顶层 help 现在靠 null 判定），单层调用方全部要改；二态塞三态语义混乱。
- B — 新增 `Resolve(argv) → CommandResolution`（三态：match/help/unknown），`Match`/`SubcommandMatch` 原样保留作单层 sugar。优：加法式、零破坏、嵌套语义独立清晰；缺：API 多一个入口。

**决定：** 选 B。pre-1.0 虽不背兼容包袱，但这里 `Match` 不是"旧设计妥协"而是"单层场景的简洁正解"，与 `Resolve` 是不同抽象层次的两个有效入口（类比 `cargo` 既有顶层也有子命令）。`Resolve` 的三态正交语义比把三态硬塞进 `Match` 的 null/非null 二态更干净。新消费端（xtask/launcher）一律用 `Resolve`。

### Decision 2: 子 router 用 parallel arrays，与现有 `Add` 存储并列

**问题：** 一个节点要同时存"叶子 ArgParser 子命令"和"子 router 子命令"。z42 无 union 类型。

**决定：** 在 `SubcommandRouter` 上加一组与现有 `_names/_descs/_parsers` 并列的 `_childRouters` 数组 + `_isRouter` 标志数组（parallel array 模式，与本库既有 `_optionRequired` 等一致）：
- `_names[i]` / `_descs[i]` — 子命令名 + 描述（叶子与子 router 共用）
- `_parsers[i]` — 叶子 ArgParser（`_isRouter[i]==false` 时有效）
- `_childRouters[i]` — 子 SubcommandRouter（`_isRouter[i]==true` 时有效）
- `_isRouter[i]` — 该槽是叶子还是子树

`Add` 写 `_parsers[i]` + `_isRouter=false`；`AddRouter` 写 `_childRouters[i]` + `_isRouter=true`。`_grow` 同步扩两组数组。

> 备选（嵌套 wrapper 类 `_Node`）被否：parallel array 与本库现有风格（`_RepeatedList`/`_MutexGroup` 之外一律 parallel array）一致，改动面更小。

### Decision 3: `CommandResolution` 三态 + 统一 `HelpText()`/`Path()`

```
public sealed class CommandResolution {
    // 恰一个为 true
    bool IsMatch();      // 命中叶子且非 help
    bool IsHelp();       // 任一层请求 help（含 router 层无 token / 叶子 ShowHelp）
    bool IsUnknown();    // 任一 router 层遇未知 token

    string[]    Path();         // 已下钻/命中的子命令链
    ParseResult Result();       // IsMatch 时有效（叶子解析结果）
    string      HelpText();     // IsHelp / IsUnknown 时有效（对应层帮助）
    string      ErrorMessage(); // IsUnknown 时有效
}
```

**消费端范式（xtask/launcher ② 用）：**

```z42
CommandResolution res = root.Resolve(Environment.GetCommandLineArgs());
if (res.IsHelp())    { Console.WriteLine(res.HelpText()); Environment.Exit(0); }
if (res.IsUnknown()) { ConsoleError.WriteLine(res.ErrorMessage()); Console.WriteLine(res.HelpText()); Environment.Exit(2); }
// IsMatch:
string[] path = res.Path();   // 例 ["build","package"]
ParseResult r = res.Result();
// 按 path dispatch handler …
```

叶子 help（`build package -h`）与 router help（`build -h`）在消费端**走同一个 `IsHelp` 分支**——这是设计目标：调用方不必区分"哪层 help"，库已把对应层 `HelpText()` 准备好。

### Decision 4: `Resolve` 的递归与 help/unknown 判定顺序

每层算法（router 节点，入参 `argv` 为已剥离父前缀的剩余参数）：

1. `argv` 为空 **或** `argv[0]` 是 `-h`/`--help` → `IsHelp`，`HelpText` = 本 router `HelpText()`，`Path` = 当前累积前缀。
2. 在本 router 找 `argv[0]`：
   - 未找到 → `IsUnknown`，`ErrorMessage` = `"<programName>: unknown command '<tok>'"`，`HelpText` = 本 router `HelpText()`。
   - 命中子 router → 递归 `child.Resolve(argv[1..])`，把当前名 push 进 `Path`。
   - 命中叶子 ArgParser → `pr = parser.Parse(argv[1..])`；若 `pr.ShowHelp()` → `IsHelp`，`HelpText` = `parser.HelpText()`；否则 `IsMatch`，`Result` = `pr`。`Path` push 当前名。

> 叶子 `Parse` 抛 `CliException`（未知 flag / 缺 value / 缺 positional 等）时**不在 `Resolve` 内吞**——按现有库契约向上抛，消费端 try/catch（与现有 `ArgParser.Parse` 用法一致）。`Resolve` 只负责路由与 help/unknown-subcommand 分流，不接管 option 级错误。

## Implementation Notes

- `Path()` 累积：采用**回溯前插**（prepend-on-unwind）——每层 router 调用子节点的**公有** `Resolve(rest)` 拿到子 `CommandResolution`，再 `childRes.Prepend(thisName)` 把自己的名插到 path 头部。选这个而非"自顶向下传 prefix"，是为了**只用公有 `Resolve` 完成递归**，规避同类型跨实例私有成员访问（z42 语义不确定）。代价是每层重建一次 path 数组，但命令树仅 2~3 层、path 极短，开销可忽略。`Prepend` 原地改 `_path` 并返回 `this` 供链式。
- 子 router 的 `programName` 由构造方显式给全路径（如 `new SubcommandRouter("xtask build", ...)`），`HelpText()` 直接用它——与现有 `programName` 约定一致，不在 `AddRouter` 内自动拼接（保持显式、零魔法）。
- `CommandResolution` 用私有构造 + 静态工厂（`_match`/`_help`/`_unknown`）或直接公有字段赋值，按库内既有风格（`SubcommandMatch` 是公有构造）——采用小型公有构造 + 三个布尔标志，三态由工厂内部保证互斥。
- 行数控制：`SubcommandRouter.z42` 现 170 行，新增 `AddRouter`/`Resolve`/递归 helper/`CommandResolution` 预计 +120~150 行，仍在 300 软限内；若逼近软限，把 `CommandResolution` 拆到独立文件 `src/CommandResolution.z42`（NEW，届时回阶段 3 更新 Scope）。

## Testing Strategy

- 单元（[Test] in `tests/cli_nested_subcommand.z42`）：覆盖 spec 全部 scenario —— 2 层/3 层命中、顶层/中间层/叶子层 help、顶层/中间层 unknown、三态互斥、叶子 `CliException` 透传。
- 回归：现有 `cli_subcommand.z42`（单层 `Match`）必须仍绿——验证 Decision 1 的"零破坏"。
- VM 验证：`z42 xtask.zpkg test lib z42.cli`（该 lib 的 [Test]）+ 完整 `z42 xtask.zpkg test`。
- Golden：如 stdlib 测试体系需要 `.zbc` golden，`xtask regen` 生成并纳入提交。
