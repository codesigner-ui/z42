# Spec: Std.Process

## ADDED Requirements

### Requirement: argv-style 命令调用

`Std.Process` 接受 argv 数组形式的命令调用，**禁止**单字符串"命令行"形态。

#### Scenario: 简单 Run 捕获 stdout + ExitCode

- **WHEN** z42 用户写
  ```z42
  var r = new Process("echo").Arg("hello").Run();
  ```
- **THEN** 子进程 `echo` 被启动，参数 `["hello"]`
- **AND** `r.ExitCode == 0`
- **AND** `r.Stdout == "hello\n"`（POSIX）或 `"hello\r\n"`（Windows 工具）
- **AND** `r.Stderr == ""`

#### Scenario: 参数中含空格不需要 quoting

- **WHEN** z42 用户写
  ```z42
  new Process("printf").Arg("%s\n").Arg("a b c").Run();
  ```
- **THEN** `printf` 收到两个 argv：`"%s\n"` 和 `"a b c"`
- **AND** **不**会被 shell 切成 5 个 token
- **AND** stdout 是 `"a b c\n"`

#### Scenario: argv 数组一次性传入

- **WHEN** z42 用户写
  ```z42
  new Process("git").Args(["log", "--oneline", "-n", "3"]).Run();
  ```
- **THEN** 等价于链式 4 次 `.Arg(...)`
- **AND** `Args(string[])` 把数组每个元素 append 到 argv，**不**做空白切分

### Requirement: 不走 shell

调用过程**不经过** `/bin/sh -c` 或 `cmd.exe /c`。

#### Scenario: glob 不展开

- **WHEN** z42 用户写
  ```z42
  new Process("ls").Arg("*.txt").Run();
  ```
- **THEN** `ls` 收到字面 argv `["*.txt"]`，而不是 cwd 下匹配的文件列表
- **AND** 若 cwd 没有名字叫 `*.txt` 的文件，`ls` 报 "no such file"
- **AND** z42 用户要做 glob，应改用 `Directory.EnumerateFiles` 或类似 API

#### Scenario: 变量 / 重定向符号不被解释

- **WHEN** z42 用户写
  ```z42
  new Process("echo").Arg("$HOME").Arg(">").Arg("out.txt").Run();
  ```
- **THEN** `echo` 收到字面 `["$HOME", ">", "out.txt"]`
- **AND** stdout 字面打印 `$HOME > out.txt`（无变量展开 / 重定向）

### Requirement: 可执行文件查找

`argv[0]` 按平台规则解析，运行时屏蔽 Unix / Windows 差异。

#### Scenario: 裸名查 PATH

- **WHEN** z42 用户写 `new Process("cargo").Run()`，cwd 任意
- **THEN** 运行时按 PATH 顺序查找可执行文件
- **AND** Windows 上额外按 `PATHEXT` 试 `.EXE` / `.CMD` / `.BAT` 后缀
- **AND** 找不到 → 抛 `Std.ProcessStartException`（**不**进入"启动了但 exit != 0" 路径）

#### Scenario: 相对路径相对 cwd（不是 WorkingDirectory）

- **WHEN** z42 用户写
  ```z42
  // cwd = /home/user
  new Process("./build.sh")
      .WorkingDirectory("/tmp")
      .Run();
  ```
- **THEN** 运行时尝试启动 `/home/user/build.sh`（**相对 cwd**）
- **AND** 子进程的初始 cwd 是 `/tmp`（`.WorkingDirectory()` 生效）
- **AND** 这一行为符合 Rust `std::process::Command` 与 sh `cd /tmp && /home/user/build.sh`

#### Scenario: 绝对路径直接用

- **WHEN** z42 用户写 `new Process("/usr/bin/env").Run()`
- **THEN** 不查 PATH，直接 fork-exec / CreateProcess

### Requirement: stdio 三态 + 文件重定向

`Stdio` 是 sealed class，公开 `Inherit` / `Pipe` / `Null` 三个单例 +
`ToFile(path)` 工厂方法。

#### Scenario: Run 默认 capture（stdout / stderr = Pipe）

- **WHEN** z42 用户调 `.Run()` 没显式设过 `.Stdout(...)` / `.Stderr(...)`
- **THEN** stdout / stderr 都默认 `Stdio.Pipe`，由父进程捕获
- **AND** `ProcessResult.Stdout` / `.Stderr` 含完整字节
- **AND** stdin 默认 `Stdio.Null`（无输入）

#### Scenario: Spawn 默认 inherit

- **WHEN** z42 用户调 `.Spawn()` 没显式设过 stdio
- **THEN** 三个 stdio 都默认 `Stdio.Inherit`，子进程直接读写父 stdio
- **AND** `ProcessHandle` 不暴露 `Stdout` / `Stderr` 字段（无东西可拿）

#### Scenario: 重定向到文件

- **WHEN** z42 用户写
  ```z42
  new Process("cargo").Arg("build")
      .Stdout(Stdio.ToFile("build.log"))
      .Stderr(Stdio.ToFile("build.log"))   // 同一文件 OK，由 OS dup
      .Run();
  ```
- **THEN** `build.log` 含子进程合并的 stdout + stderr 字节
- **AND** Run() 返回的 `ProcessResult.Stdout == ""`（被重定向到文件，未 capture）

#### Scenario: Null 黑洞

- **WHEN** z42 用户把 stdout 设为 `Stdio.Null`
- **THEN** 子进程往 stdout 写的内容被丢弃
- **AND** 子进程 stdout 的 fd 指向 `/dev/null`（Unix）或 `NUL`（Windows）

### Requirement: 退出码 + EnsureSuccess

#### Scenario: exit 0 时 EnsureSuccess 返回结果对象

- **WHEN** 子进程正常退出（exit 0）
- **THEN** `r.ExitCode == 0`
- **AND** `r.EnsureSuccess()` 不抛异常，返回 `r` 本身（便于链式）

#### Scenario: exit != 0 时 EnsureSuccess 抛异常

- **WHEN** 子进程 exit 非 0
- **THEN** `r.EnsureSuccess()` 抛 `Std.ProcessExitException`
- **AND** 异常 `Message` 含 `ExitCode` 数值 + 命令名 + 截断的 stderr（前 1024 字节）
- **AND** 异常对象暴露 `ExitCode` / `Stdout` / `Stderr` 三个字段供 catch 后查看
- **AND** `r.ExitCode != 0` 时**不**自动抛——只在 `EnsureSuccess()` 时抛

### Requirement: 环境变量

#### Scenario: 默认继承父进程 env

- **WHEN** z42 用户 `.Run()` 未调用过 `.Env(...)` / `.ClearEnv()`
- **THEN** 子进程看到父进程全部环境变量（含 `PATH` / `HOME` 等）

#### Scenario: 增删改单条

- **WHEN** z42 用户写 `.Env("RUSTFLAGS", "-C opt-level=3")`
- **THEN** 子进程多出 `RUSTFLAGS=-C opt-level=3`，其余继承
- **AND** value 为空字符串 → 设置为空字符串（不是 unset）
- **AND** `.EnvRemove("HOME")` 显式移除某 key

#### Scenario: 完全替换

- **WHEN** z42 用户写
  ```z42
  .ClearEnv().Env("PATH", "/usr/bin").Env("LANG", "C")
  ```
- **THEN** 子进程只看到 `PATH` + `LANG` 两条 env，其余被清空

### Requirement: WorkingDirectory

#### Scenario: 子进程初始 cwd 切换

- **WHEN** z42 用户写 `.WorkingDirectory("/tmp")`
- **THEN** 子进程启动时 cwd 是 `/tmp`
- **AND** 父进程的 cwd 不变

#### Scenario: 未设置时继承父 cwd

- **WHEN** 没调过 `.WorkingDirectory(...)`
- **THEN** 子进程 cwd 与父进程 cwd 一致

### Requirement: Stdin 数据写入

#### Scenario: 一次性 bytes（Run 糖）

- **WHEN** z42 用户写
  ```z42
  new Process("base64")
      .StdinBytes([0x48, 0x69])   // "Hi"
      .Run();
  ```
- **THEN** 子进程 stdin 读到 2 字节 → EOF
- **AND** `Run()` 在子进程退出后返回

#### Scenario: 一次性 string（UTF-8 编码）

- **WHEN** `.StdinString("hello\n")`
- **THEN** 子进程 stdin 读到 UTF-8 编码的 6 字节 → EOF

#### Scenario: 流式（Spawn 后写）

- **WHEN** z42 用户写
  ```z42
  var h = new Process("cat").Stdin(Stdio.Pipe).Stdout(Stdio.Pipe).Spawn();
  h.WriteStdin([0x41]);
  h.WriteStdin([0x42]);
  h.CloseStdin();
  var r = h.Wait();
  ```
- **THEN** `cat` 收到 `"AB"`，stdout 也回放 `"AB"`
- **AND** `r.Stdout` 等于 `"AB"`（UTF-8 解码）

### Requirement: Timeout

#### Scenario: 子进程在 timeout 前正常退出

- **WHEN** z42 用户写
  ```z42
  new Process("sleep").Arg("1")
      .Timeout(TimeSpan.FromSeconds(5))
      .Run();
  ```
- **THEN** Run 在 ~1 秒后正常返回，`ExitCode == 0`
- **AND** 不抛异常

#### Scenario: 子进程超时

- **WHEN** z42 用户写
  ```z42
  new Process("sleep").Arg("10")
      .Timeout(TimeSpan.FromSeconds(1))
      .Run();
  ```
- **THEN** ~1 秒后 z42 runtime 调 `KillForce`（Unix SIGKILL / Windows TerminateProcess）
- **AND** Run 抛 `Std.ProcessTimeoutException`
- **AND** 异常 `Message` 含 timeout 数值 + 命令名

### Requirement: Spawn / 异步式控制

#### Scenario: Spawn 立即返回 handle

- **WHEN** z42 用户调 `.Spawn()`
- **THEN** 父进程不阻塞，立即拿到 `ProcessHandle`
- **AND** `h.Pid` 暴露子进程 PID
- **AND** `h.Wait()` 阻塞直到结束并返回 `ProcessResult`
- **AND** `h.TryWait()` 立即返回 `ProcessResult?`（非阻塞，子进程未结束返回 null）

#### Scenario: handle.Kill 发 SIGTERM

- **WHEN** z42 用户调 `h.Kill()`
- **THEN** Unix 发 SIGTERM、Windows 调 TerminateProcess（Windows 无 SIGTERM 语义，
  TerminateProcess 等价于 KillForce —— 与 Rust `std::process` 一致）
- **AND** 子进程退出后 `h.Wait()` 返回的 `ExitCode` 反映被信号杀（Unix 一般是
  128 + signal_no = 143；Windows 是 1）

#### Scenario: handle.KillForce 发 SIGKILL

- **WHEN** z42 用户调 `h.KillForce()`
- **THEN** Unix 发 SIGKILL（不可被子进程拦截），Windows 同 TerminateProcess

### Requirement: 异常分类

`Std.ProcessStartException` 与 `Std.ProcessExitException` 是**独立**类型，
继承自 `Std.Exception`，可以分别 catch。

#### Scenario: 找不到可执行文件

- **WHEN** z42 用户 `.Run()` 一个不存在的命令名
- **THEN** 抛 `Std.ProcessStartException`
- **AND** Message 含尝试的可执行文件名 + 底层错误（ENOENT / not found）
- **AND** **不**抛 `ProcessExitException`（区分"启动失败" vs "启动了但失败"）

#### Scenario: exit != 0 + EnsureSuccess

- **WHEN** 子进程 exit 1 且 z42 用户调 `EnsureSuccess()`
- **THEN** 抛 `Std.ProcessExitException`
- **AND** **不**抛 `ProcessStartException`

#### Scenario: timeout

- **WHEN** Run 因 `.Timeout(...)` 超时
- **THEN** 抛 `Std.ProcessTimeoutException`
- **AND** 三类异常的 FQ 名分别是 `Std.ProcessStartException` /
  `Std.ProcessExitException` / `Std.ProcessTimeoutException`，z42 catch by class

### Requirement: 二进制 + UTF-8 双路捕获

#### Scenario: 默认 UTF-8 string

- **WHEN** `.Run()` 返回 `r`
- **THEN** `r.Stdout: string` 是 UTF-8 解码后的字符串
- **AND** 无效 UTF-8 字节用 U+FFFD 替换（lossy decode，与 `String.FromUtf8Lossy` 同语义）

#### Scenario: 二进制访问

- **WHEN** 子进程 stdout 不是 UTF-8（如 zlib 压缩字节流）
- **THEN** 用 `r.StdoutBytes: byte[]` 拿原字节
- **AND** `r.StderrBytes: byte[]` 同理

## MODIFIED Requirements

无（纯新增 stdlib 类）。

## IR Mapping

无新 IR 指令。`Std.Process` 全部走 Tier 1 [Native] dispatch（`CallNative` 指令
已存在），不改 IR 层。

## Pipeline Steps

- [ ] Lexer — 不影响
- [ ] Parser / AST — 不影响
- [ ] TypeChecker — 不影响（stdlib 标准类发现）
- [ ] IR Codegen — 不影响（沿用 CallNative）
- [ ] VM interp — 不影响
- [x] **stdlib（z42.io）** — 新增 4 个 .z42 文件 + manifest 更新
- [x] **Native crate（z42.process-native）** — 新增 Rust crate，包装 std::process::Command
