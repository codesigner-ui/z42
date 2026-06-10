# Design: xtask + launcher 迁移到 Std.Cli

## Architecture

两个 CLI 各建一棵 `Std.Cli.SubcommandRouter` 树，`Main` 调 `Resolve(argv)` 得 `CommandResolution`，按 `IsHelp`/`IsUnknown`/`IsMatch` 三分支处理；`IsMatch` 时按 `Path()` 派发到 handler，handler 从 `Result()`（`ParseResult`）读参数。

### xtask 目标命令树

```
xtask                         (root router)
├── build            (router)  runtime|compiler|compiler-z42|stdlib|launcher|all  (各 leaf)
│   └── stdlib       (leaf)    positional [lib]
├── package          (leaf)    [release|debug] --rid R --variant V        ← 提顶层
├── feature-matrix   (leaf)    （无参）                                    ← 提顶层
├── test             (router*) all|vm|cross-zpkg|dist|stdlib|compiler|compiler-z42|changed
│   ├── vm           (leaf)    [interp|jit] --no-rebuild --jobs N
│   ├── cross-zpkg   (leaf)    [interp|jit]
│   ├── dist         (leaf)    [interp|jit]
│   ├── stdlib       (leaf)    [lib]
│   └── changed      (leaf)    [base] --dry-run
├── deps             (router)  check (leaf) | install (leaf: --os --check --drift --print-env --force [node|android-emulator])
├── bench            (router*) stdlib [lib]  | (default leaf: --quick --diff --current --baseline --threshold-time --threshold-memory --quiet)
├── regen            (leaf)    [release] --no-stdlib
├── audit            (leaf)
├── clean            (leaf)    [tests|bench|all]
└── run              (leaf)    透传到 launcher（保留 thin alias）
```

`router*` = 带"默认动作"的混合节点（见 Decision 3）。

### launcher 目标命令树

```
z42                          (root router)
├── info|list                (leaf, 无参)
├── default                  (leaf, [version])
├── link                     (leaf, <dir> --as <ver>)
├── which                    (leaf, --runtime V)
├── run                      (leaf, --runtime V <app> [-- app-args])   ← Decision 4
├── install|uninstall        (leaf, <version>)
└── apphost                  (router) → build (leaf, <app|toml> --out P)
```
顶层另保留两个**非注册**捷径（Decision 5）：`z42 <app.zpkg|.zbc> [-- args]` apphost 简写。

## Decisions

### Decision 1: handler 从 ParseResult 读参，不再扫 argv

每个 leaf 声明 `ArgParser`（flag/option/positional）。`Resolve` 命中后，handler 签名从 `(string[] argv)` 改为 `(ParseResult r)`（或 `(CommandResolution res)`），内部 `r.GetOption("rid")` / `r.GetFlag("no-rebuild")` / `r.GetPositional(0)` 取值。删除所有 `int i = 2; while …` 扫描循环。委派型函数（`_packageDesktop`/`_testCrossZpkgImpl` 等）签名不变。

### Decision 2: 顶层经 Resolve 统一三分支，删手写 _help

`Main`：
```z42
CommandResolution res = root.Resolve(argv);
if (res.IsHelp())    { Console.WriteLine(res.HelpText()); Environment.Exit(0); }
if (res.IsUnknown()) { ConsoleError.WriteLine(res.ErrorMessage());
                       Console.WriteLine(res.HelpText()); Environment.Exit(2); }
_dispatch(res.Path(), res.Result());
```
手写 `_help()`（xtask 43 行 + launcher）删除，帮助全由 router/ArgParser `HelpText()` 生成。`_dispatch` 按 `Path()` 串接（如 `["build","stdlib"]` → `_buildStdlib(r)`）。

### Decision 3: 混合节点（默认动作 + 子命令）——消费端薄 shim

`test` 与 `bench` 既有命名子命令、又有"无子命令时的默认动作"，`Std.Cli` router 不建模"默认 leaf"。**不改库**（库出本变更 Scope），改在消费端薄处理：

- `test`：
  - argv 为空 **或** 首 token 以 `-` 开头（如 `--parallel`）→ `_testAll()`（保留现有隐式行为）。
  - 首 token 是已知子命令 → 走 `test` 子 router 的 `Resolve`。
  - `test -h` → 子 router help。
  - 首 token 非 `-` 且非已知子命令 → unknown（报错 + help）。
- `bench`：
  - 首 token 是 `stdlib`/`lib` → per-lib（注：`lib` 别名在 bench 同样删，只留 `stdlib`）。
  - 否则把剩余 argv 交 `bench` 默认 leaf 的 `ArgParser`（声明 `--quick`/`--diff`/`--current`/…）解析 → e2e。
  - `bench -h` → 列默认 flag + `stdlib` 子命令。

该 shim 是**少量、被测试覆盖、且有文档**的例外；clean 的层级命令（build/deps/package/…）全部纯 `Resolve`。

> 为何不给 Std.Cli 加"默认 leaf"特性：那是 stdlib 改动（本 toolchain 变更不碰 stdlib，且 stdlib 锁另有持有者）。若未来多个消费端都需要，再单开 stdlib 变更抽象之（记 Deferred）。

### Decision 4: launcher `run` 的 `--` app-args 透传 —— ArgParser 前先切分

`Std.Cli.ArgParser` 不识别 `--` 透传分隔符。`run` handler 在交 ArgParser 前，先按第一个 `--` 把 argv 切成 `[head | appArgs]`：head（`--runtime V <app>`）交 ArgParser，`appArgs` 原样透传给被运行程序。同法适用任何需要"透传尾参"的命令。

### Decision 5: apphost 简写保留为顶层非注册捷径

`z42 <app.zpkg> [-- args]` 不是注册子命令。`Main` 在 `Resolve` **之前**先判：若 `argv[0]` 以 `.zpkg`/`.zbc` 结尾 → 直接 `_cmdRun(argv)`（含 `--` 切分）。其余交 `Resolve`。保持现有 UX。

### Decision 6: 命令树重组的 CI 联动

`package` 提顶层 → 同步改 5 处 CI 调用（`build package` → `package`）。`feature-matrix` 提顶层零 CI 影响（CI 直接 cargo）。删 `lib` 别名 → 改 2 个 doc。均在同一变更内闭环（破坏面同周期清零）。

## Implementation Notes

- **bootstrap 编译器陷阱**（来自 add-cli-nested-subcommands）：类方法内对同类型另一实例调方法需先赋值带类型局部变量。consumer 是自由函数 + ArgParser/Router（不同类型），一般不触发；若 router 树构造里递归引用注意。
- **依赖**：xtask / launcher 的 `*.z42.toml` 需含 `z42.cli` 依赖（xtask 经 add-xtask-cli 可能已有；launcher 大概率需新增）。
- **行数**：`xtask.z42` 现 321 行，router 树构造会增量；若超 300 软限，把"建树"抽到 `xtask_cli.z42`（NEW，届时回阶段 3 更新 Scope）。launcher 同理。
- **验证不能靠 repo-root `./xtask`**（bundled vm 陈旧 0.13）：全程用 `artifacts/build/runtime/release/z42vm`（0.14+）+ `Z42_PORTABLE_VM`/`Z42_LIBS` 旁路跑 `xtask.zpkg`。

## Testing Strategy

- **兼容向量**（spec Behavior Invariants 表）：逐条本地跑，断言解析与行为不变（尤其 CI 用到的 `test vm jit --jobs=4`、`bench --diff …`、`package release --rid`、`regen --no-stdlib`）。
- **每层 help**：`xtask -h` / `xtask build -h` / `xtask package -h` / `z42 run -h` / `z42 apphost build -h` 各打印对应帮助、退出 0、不执行动作。
- **未知**：`xtask bogus` / `xtask build bogus` / `xtask package --no-such-flag` → 报错 + help。
- **完整 GREEN gate**：`xtask test`（经 fresh vm 旁路）—— compiler + vm + cross-zpkg + stdlib 全绿（且 gate 本身就跑在迁移后的 xtask 上，是端到端自证）。
- 单测：xtask/launcher 无既有 [Test] 框架（它们是 app 非 lib）；解析正确性靠上述命令级 e2e 向量覆盖（spec 要求"新 CLI 命令解析正确性 + 错误输入报错"经 e2e 满足）。
