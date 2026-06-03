# Spec: z42 launcher (P1)

## ADDED Requirements

### Requirement: z42vm 透传命令行参数

#### Scenario: `--` 后参数到达程序
- **WHEN** `z42vm app.zbc Main --mode interp -- alpha beta`
- **THEN** 程序内 `Environment.GetCommandLineArgs()` 返回 `["alpha", "beta"]`(不含 VM 自身 flag)

#### Scenario: 无 `--` 时为空
- **WHEN** `z42vm app.zbc Main`(无 `--`)
- **THEN** `GetCommandLineArgs()` 返回空(保持现状,不回归)

### Requirement: z42c 为 script emit Exe-zpkg

#### Scenario: 单文件 build 产 Exe 模式 + entry
- **WHEN** `z42c build hello.z42`(hello 含 `Main()`)
- **THEN** 产出 zpkg 的 mode = `Exe`,`META.entry` = 探测到的 `Main` 限定名

#### Scenario: 无 Main 报错
- **WHEN** script 无 `Main()`
- **THEN** 明确报错"no Main()",非零退出

### Requirement: trampoline 转发到 z42 核心

#### Scenario: 原样转发 argv
- **WHEN** `z42 <任意参数...>`
- **THEN** trampoline 用 `~/.z42/launcher/z42vm` 跑 `launcher.zpkg`,把全部 argv 经 `--` 原样传入;回传核心退出码

#### Scenario: launcher 运行时缺失
- **WHEN** `~/.z42/launcher/` 不完整
- **THEN** 明确报错 + 重装指引,非零退出

### Requirement: `z42 run`

#### Scenario: 本地默认版本运行 + 透传
- **WHEN** 已 `z42 default dev`;执行 `z42 run app.zpkg -- x y`
- **THEN** 用 `runtimes/dev/z42vm` 跑 `app.zpkg`,程序 argv 得 `[x, y]`,回传其退出码

#### Scenario: 显式版本
- **WHEN** `z42 run --runtime 0.3.4 app.zpkg`
- **THEN** 用 `runtimes/0.3.4/z42vm` 运行;该版本未装则报错列已装

#### Scenario: 裸形式
- **WHEN** `z42 app.zpkg -- x`
- **THEN** 等价 `z42 run app.zpkg -- x`

### Requirement: 运行时管理(本地)

#### Scenario: link 本地构建为某版本
- **WHEN** `z42 link artifacts/build/.../release --as dev`
- **THEN** `runtimes/dev/` 指向/拷贝该目录的 z42vm + libs;`z42 list` 显示 `dev`

#### Scenario: list / default / which / info
- **WHEN** 分别执行 `z42 list` / `z42 default dev` / `z42 which app.zpkg` / `z42 info`
- **THEN** 列已装版本 / 写默认版本 / 打印解析到的 z42vm 路径 / 打印 Z42_HOME+已装+默认+配置

### Requirement: 版本解析顺序

#### Scenario: 优先级
- **WHEN** 同时存在 `--runtime`、默认版本
- **THEN** 解析顺序:`--runtime` > app 自带声明(P1 恒空)> `~/.z42` default > 唯一已装;无法确定时报错并列候选

## Pipeline / 组件影响

- VM:`src/runtime/src/main.rs`(Cli 收尾 args)+ `vm_context.rs`(存 argv)+ env corelib(GetCommandLineArgs)
- 编译器:`z42.Driver/BuildCommand.cs`(script→Exe-zpkg)
- 新工具:`src/toolchain/launcher/`(trampoline Rust + 核心 z42)

## MODIFIED Requirements

**Before:** 跑 z42 程序经 `z42vm`(裸,无 argv)或 `z42c run`(编译器兼 runner,不转发参数)。
**After:** 统一经 `z42` launcher → 选版本 → z42vm 跑 Exe-zpkg + 透传 argv;z42c 退回纯编译器(emit Exe-zpkg)。
