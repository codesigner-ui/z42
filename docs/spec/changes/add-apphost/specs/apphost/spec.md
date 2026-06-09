# Spec: apphost（每-app 原生可执行文件，framework-dependent）

## ADDED Requirements

### Requirement: apphost 占位符 patch

apphost 模板内嵌一段固定大小占位字节区（MAGIC sentinel + payload），`z42 apphost build` 在二进制文件层面把 payload 覆写为目标 app 的 zpkg 相对路径。

#### Scenario: patch 写入目标路径
- **WHEN** 对未配置的 apphost 模板执行 `z42 apphost build dist/myapp.zpkg`
- **THEN** 产出 `dist/myapp`，其占位区 payload 被覆写为 `myapp.zpkg`（UTF-8 + NUL 终止），MAGIC sentinel 保持不变

#### Scenario: 未配置模板拒绝运行
- **WHEN** 直接运行一个未经 patch 的 apphost 模板（payload 全 0）
- **THEN** stub 打印"apphost 未配置目标"类错误并以非零码退出，不尝试解析运行时

#### Scenario: 改名安全（内嵌路径相对 exe 目录）
- **WHEN** 把 `dist/myapp` 改名为 `dist/tool` 后运行 `./dist/tool`
- **THEN** stub 仍按内嵌 payload 定位 `dist/myapp.zpkg`（不依赖 exe 文件名），正常起 app

#### Scenario: 超长路径报错不截断
- **WHEN** 目标 zpkg 相对路径长度超出 payload 容量
- **THEN** `z42 apphost build` 报错退出，不产出被截断的 apphost

### Requirement: apphost 本地优先的 framework-dependent 运行时解析

apphost stub 按固定优先级定位 launcher 运行时（`z42vm` + `launcher.zpkg` + `libs`），本地目录优先于系统目录；apphost 自身**不**捆绑运行时。

#### Scenario: Z42_HOME 覆写最高优先
- **WHEN** 设置了非空 `$Z42_HOME` 且 `$Z42_HOME/launcher/` 含完整运行时
- **THEN** 无论本地/系统是否也有运行时，stub 用 `$Z42_HOME/launcher/` 的运行时

#### Scenario: 本地 .z42 优先于系统
- **WHEN** 未设 `$Z42_HOME`，且 exe 目录上行某级存在 `.z42/`（含完整运行时），同时 `$HOME/.z42/` 也存在运行时
- **THEN** stub 选**本地** `.z42/` 的运行时（本地遮蔽系统）

#### Scenario: 回退系统安装
- **WHEN** 未设 `$Z42_HOME`，exe 目录上行各级均无 `.z42/` 运行时，但 `$HOME/.z42/launcher/` 有
- **THEN** stub 用 `$HOME/.z42/launcher/` 的运行时

#### Scenario: 无运行时报错
- **WHEN** `$Z42_HOME`、本地上行、`$HOME/.z42` 三处均无可用运行时
- **THEN** stub 打印"未找到 z42 运行时（装 z42 或设 Z42_HOME）"并列出已查路径，非零退出

### Requirement: apphost 起 app 并透传

apphost 复用 launcher 核心的裸 apphost 形式起 app，透传命令行参数与退出码，并设 `Z42_LIBS`。

#### Scenario: 经 launcher 核心起 app
- **WHEN** 运行 `./myapp`（已解析到运行时 R、内嵌 app = `myapp.zpkg`）
- **THEN** stub `exec` `R/z42vm R/launcher.zpkg -- <myapp.zpkg 路径> --`，并设环境 `Z42_LIBS = R/libs`

#### Scenario: 参数透传
- **WHEN** 运行 `./myapp foo bar`
- **THEN** app 经 `Std.IO.Environment.GetCommandLineArgs()` 见到 `[foo, bar]`（apphost 在 app.zpkg 后注入 `--` 再接用户 argv）

#### Scenario: 退出码回传
- **WHEN** app 以退出码 N 结束
- **THEN** `./myapp` 进程以退出码 N 结束

## MODIFIED Requirements

### Requirement: launcher trampoline 与 apphost 共享解析逻辑

**Before:** trampoline (`z42`) 在 `main.rs` 内联 `resolve_runtime`（installed → portable）。

**After:** `z42_home` / 运行时探测 / `exec` 抽到 `src/lib.rs`；trampoline 改调 lib（行为不变：仍 installed → portable）；apphost bin 复用同一 lib，按本地优先顺序拼装。trampoline 对外行为零变化。

## IR Mapping

N/A —— 本变更是 toolchain（原生 stub + z42 patcher 命令），不涉及 IR / zbc 指令。

## Pipeline Steps

N/A —— 不经编译器 pipeline（Lexer/Parser/TypeChecker/Codegen）。受影响组件：
- [x] 原生 launcher crate（Rust：新 lib + apphost bin）
- [x] launcher 核心（z42：新 `apphost build` 命令）
- [x] VM `Z42_LIBS` 衔接（只读依赖，不改 VM）
