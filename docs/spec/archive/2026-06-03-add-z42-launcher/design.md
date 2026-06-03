# Design: z42 launcher

## Architecture

```
用户  ──►  z42 (原生 trampoline, ~百行 Rust)
                │  定位 ~/.z42/launcher/{z42vm, launcher.zpkg}
                ▼
          z42vm(launcher 运行时, pinned)  launcher.zpkg  --  <用户 argv 原样>
                │  ← 这层之后全是 z42 代码
                ▼
          launcher 核心(z42):解析 argv → 子命令
                │  run:  resolve 版本 → Std.IO.Process.Spawn
                ▼
          ~/.z42/runtimes/<ver>/z42vm  <app.zpkg>  --  <app args>
```

两类运行时,物理分开:
- **launcher 运行时** `~/.z42/launcher/`:固定、随 launcher 一起装,只用来跑 launcher 核心自己。
- **app 运行时** `~/.z42/runtimes/<ver>/`:受 launcher 管理,用来跑用户 app。

### ~/.z42 磁盘布局（`Z42_HOME` 可覆盖）
```
~/.z42/
├── bin/z42                     # trampoline(放 PATH)
├── launcher/                   # launcher 自身的运行时(pinned)
│   ├── z42vm
│   ├── launcher.zpkg
│   └── libs/                   # stdlib zpkg
├── runtimes/<ver>/{z42vm, z42c?, libs/}
├── config.toml                 # default 版本 / (P2)安装源
└── cache/downloads/            # P2
```

## Decisions

### D1: trampoline 极小化,逻辑全 z42
**问题:** "z42 优先"与"无 VM 跑不了 z42"如何兼得?
**决定:** 原生只留 trampoline(找 launcher 运行时 + exec + 回传码);argv 解析/缓存/resolve/起 app 全部 z42。stdlib 已具备所需 API(`Cli.ArgParser` / `IO.Process` / `IO.Directory/Path/File` / `IO.Environment`,见 scripts-port 盘点)。原生面最小、bootstrap-safe、z42 占比最大。

### D2: trampoline 用"随装的固定运行时"跑核心,避免鸡生蛋
**问题:** 跑 `launcher.zpkg` 需要一个与之版本匹配的 z42vm,但版本选择正是 launcher 的活。
**决定:** launcher 安装即捆绑 `~/.z42/launcher/{z42vm + launcher.zpkg + libs}`(strict-pin 配套)。trampoline 永远用这套跑核心;核心再去管 `runtimes/<ver>/` 给 app 用。两套运行时解耦。

### D3: app 运行经 z42vm 跑 Exe-zpkg + 透传 args(不经 z42c)
**问题:** entry 怎么来、args 怎么进?
**决定:** app = Exe-mode zpkg(`META.entry` 自带);z42vm 读 entry、并把 `--` 后的 argv 填进 `GetCommandLineArgs()`。z42c 只负责 `build` 出 Exe-zpkg。arg 透传放 z42vm(Rust 运行时,自举不重写),不放 z42c(编译器,会被 z42 重写)。

### D4: P1 不依赖任何已 defer 的东西
**决定:** 版本声明格式(META/runtimeconfig)、下载、rollForward 全部 P1 不碰。本地用 `z42 link <dir> --as <ver>` 注册构建产物 + `z42 default <ver>` 选默认,`resolve` 第 2 步(读 app 自带版本)先留空 hook。

## Implementation Notes

- **z42vm argv 透传**:`Cli` 加 `#[arg(last = true)] args: Vec<String>`(或 `trailing_var_arg`),`-- ` 后的进 `args`;在 VmContext 存一份 program argv;`GetCommandLineArgs` builtin 返回它。dev/直接调用兼容:无 `--` 时 argv 为空(现状)。
- **resolve 顺序**:`--runtime <ver>` > app 自带声明(P1 空) > `~/.z42` default > 唯一已装 / 报错列候选。
- **trampoline 定位**:`Z42_HOME` 或默认 `~/.z42`;找不到 launcher 运行时 → 明确报错指引重装。
- **dev 在仓库内**:`z42 link artifacts/build/... --as dev` + `z42 default dev`,无需打包发布。
- **LOC 限**:Rust ≤300/500、z42 核心按职责拆多文件;每个 `src/toolchain/launcher` 三层目录配 README。

## Testing Strategy

- **z42 核心 [Test]**(`tests/*.z42`,经 z42-test-runner):argv 解析、resolve 顺序(各分支)、`~/.z42` 布局读写(用临时 Z42_HOME)、`list/default/which` 输出、缺版本报错。
- **z42vm argv 透传**:Rust e2e —— `z42vm script.zbc Main -- a b c` → 程序 `GetCommandLineArgs()` 得 `[a,b,c]`;无 `--` 得空。golden/run 用例。
- **z42c Exe-zpkg**:单测 —— script `build` 产物是 Exe 模式且 `META.entry` = 探测到的 Main。
- **端到端**:trampoline → 核心 → 跑一个 hello Exe-zpkg 带 args,断言输出含 args。
- **GREEN**:`./scripts/test-all.sh --scope=full`。
- **cutover 回归**:挑 1 个已 ported 脚本(如 test-vm)改走 `z42 run` + `-- args`,确认行为不变(去掉 env-var hack)。
