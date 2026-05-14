# Design: Std.Process

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│ z42 user code                                                │
│   new Process("cargo").Arg("build").Run()                    │
└─────────────────────┬────────────────────────────────────────┘
                      │ 普通 z42 类方法调用
                      ▼
┌──────────────────────────────────────────────────────────────┐
│ src/libraries/z42.io/src/Process.z42  (z42 facade)           │
│   - 字段：_program / _args / _env_overrides / _env_clear /    │
│           _cwd / _stdin / _stdout / _stderr / _timeout_ms /   │
│           _stdin_bytes                                         │
│   - Builder methods 全部纯 z42（改字段 + return this）         │
│   - Run() / Spawn() 收集字段 → 单次 [Native("__process_run")]  │
└─────────────────────┬────────────────────────────────────────┘
                      │ 一次 CallNative，参数是 z42 array 编码
                      ▼
┌──────────────────────────────────────────────────────────────┐
│ src/runtime/src/corelib/process.rs  (Rust builtin)           │
│   builtin_process_run(args: &[Value]) -> Result<Value>       │
│     1. 解 Value 数组 → 构造 std::process::Command            │
│     2. .args() / .env() / .current_dir() / .stdin() ...      │
│     3. .spawn() / .wait_with_output() / 含 timeout 时 select │
│     4. 包结果为 z42 Object（ProcessResult 类的字段顺序）       │
│                                                              │
│   builtin_process_spawn / _wait / _kill / _write_stdin / ...│
└─────────────────────┬────────────────────────────────────────┘
                      │ Rust stdlib
                      ▼
┌──────────────────────────────────────────────────────────────┐
│ std::process::Command                                        │
│   Unix:    fork-exec, dup2, kill(SIGTERM/SIGKILL)            │
│   Windows: CreateProcess, SetStdHandle, TerminateProcess     │
└──────────────────────────────────────────────────────────────┘
```

**关键决定：z42 facade 只负责字段收集 + 调一次 builtin；不在 z42 层做
跨平台特判**。所有 Windows / Unix 差异收敛到 `std::process::Command`。

## Decisions

### Decision 1: Builder 字段编码——一次 CallNative 传整包

**问题**：每个 `.Arg(...)` / `.Env(...)` 应该立即 CallNative 拼 Command，还是
全部累积在 z42 对象里，到 `Run()` 一次性传给 Rust？

**选项**：
- A. 每次 builder 调用都 CallNative（累积在 Rust 侧 handle）
- B. 全部 z42 字段累积，`Run()` 时一次性传整个"调用描述符"

**决定**：**B**。

**理由**：
- 同样的 `Process` 实例可以多次 `.Run()`（虽然罕见，但语义自然）
- 每次 CallNative 都是固定 thread_local 状态修改 → 难以错误恢复 / GC 友好性差
- z42 字段累积 → builder 是纯不可变操作，更符合 functional API 直觉
- 一次性 marshal 一个 Object（含 string[] / Map<string,string> / int? 等）
  虽然 marshal 略复杂，但只发生一次

**Marshal 编码**：`Run()` 在 z42 层把字段打成几个并行 array 后批量传给 builtin：
- `__process_run(program, args[], env_keys[], env_vals[], env_remove[], env_clear, cwd, stdin_mode, stdin_bytes, stdout_mode, stdout_path, stderr_mode, stderr_path, timeout_ms)`
- `stdin_mode` 用 int：0=Null / 1=Inherit / 2=Pipe / 3=File
  - `stdout_path` / `stderr_path` 在非 File 模式时为 null
  - `stdin_bytes` 在非 Pipe 或没 `StdinBytes` 时为 null
- 参数多（~13 个），但都是 primitives + arrays，**marshal 走现有 L1 路径无需扩展**

### Decision 2: ProcessHandle 用 i64 pid + 内部 RC slot

**问题**：`Spawn()` 返回的 handle 怎么在 Rust 侧存活到 `Wait()`？

**选项**：
- A. 把整个 `std::process::Child` boxed 进 Object 的 `NativeData::ProcessChild`
  variant（需要扩展 NativeData enum）
- B. 在 VmContext 里挂一个 `RefCell<HashMap<u64, ChildState>>`，z42 handle
  只持 pid + slot id
- C. Leak `Box<Child>` 拿 raw ptr，z42 handle 存指针；`Wait/Kill` 时还回

**决定**：**B**（slot id pattern）。

**理由**：
- A 干净但要修改 metadata `NativeData` enum（影响序列化 / GC scanning），代价大
- C 内存泄漏隐患（z42 对象 GC 时没机制通知 Rust 释放）
- B 用 RefCell<HashMap> 加 incrementing slot id：z42 handle 字段 `_slot: i64`，
  builtin `__process_kill / _wait / _try_wait / _write_stdin / _close_stdin`
  都按 slot id 查表
- z42 `ProcessHandle.Dispose()` 调 `__process_drop` 释放 slot
- 类型 ID 防误用：slot id 包含 generation 计数器，复用后旧 handle 用会抛
  `ProcessHandleInvalidException`

**Slot 表结构（VmContext 新增字段）**：
```rust
pub(crate) processes: RefCell<HashMap<u64, ProcessSlot>>,
pub(crate) process_next_id: Cell<u64>,

struct ProcessSlot {
    child: std::process::Child,
    stdin_writer:  Option<std::process::ChildStdin>,
    stdout_reader: Option<std::process::ChildStdout>,
    stderr_reader: Option<std::process::ChildStderr>,
    spawned_at:    std::time::Instant,
    timeout:       Option<std::time::Duration>,
}
```

### Decision 3: argv[0] 解析——交给 std::process::Command

**问题**：z42 层做 PATH / PATHEXT 解析，还是丢给 `std::process::Command`？

**决定**：**完全交给 std::process::Command**。

**理由**：
- `Command::new("cargo")` 在 Unix 走 execvp（用 PATH），Windows 走 CreateProcess
  （用 PATH × PATHEXT），跨平台行为已经"对"了
- z42 自己实现 PATH 查找意味着重复一份跨平台特判逻辑，违反"std 库白给"的原则
- 例外：z42 层**不**对 argv[0] 做任何字符串预处理（不替换 `/` ↔ `\`，
  不展开 `~`），保持透明 pass-through

### Decision 4: stdin/stdout/stderr 状态机——预先决定 + 一次性配置

**问题**：`.Stdin(Stdio.Pipe)` 后调 `.StdinBytes(b)` 时，bytes 在哪存？

**决定**：**z42 层把 stdin 模式 + 一次性 bytes 都存进字段，Run() 时**：
- 若 stdin = `Pipe` 且 `_stdin_bytes != null`：Rust spawn 后立即 write_all +
  drop stdin → 等于 EOF
- 若 stdin = `Pipe` 且 `_stdin_bytes == null`：只在 `Spawn()` 路径合法，
  `Run()` 会抛"Pipe stdin requires StdinBytes() or use Spawn()"
- 若 stdin = `Inherit` / `Null` 且 `_stdin_bytes != null`：抛
  "StdinBytes conflicts with Stdio.{Inherit,Null}"

**理由**：把矛盾在 z42 facade 检查掉，不让 Rust 侧再做参数有效性校验
（错误信息也能停在 z42 stack frame 更亲切）。

### Decision 5: ProcessExitException 不自动抛，只在 EnsureSuccess() 时抛

**问题**：`Run()` 时 exit != 0 自动抛 vs 只在 `EnsureSuccess()` 时抛？

**决定**：**只在 `EnsureSuccess()` 时抛**。

**理由**：
- 不少脚本用例就是查 exit code 决定下一步（例如 `git diff --exit-code`
  exit 1 表示"有改动"，不是错误）
- 自动抛 → 用户被迫 `try/catch` 包每个 `Run()`，反模式
- `EnsureSuccess()` 名字强提示"如果不 ok 就抛"，opt-in 清晰
- 对齐 Rust `Output { status, stdout, stderr }` + 用户决定何时检查

### Decision 6: timeout 实现——线程 sleep + condvar

**问题**：`std::process::Child::wait_timeout` 不在 std 库（只 in `wait-timeout`
crate）。z42 怎么实现？

**选项**：
- A. 引入 `wait-timeout` crate 依赖
- B. 起辅助线程：父线程 `wait()` 阻塞，辅助线程 `sleep(timeout)` 后
  `child.kill()`；用 condvar 协调
- C. 在 Rust 里用 SIGCHLD + waitpid（Unix）/ WaitForSingleObject（Windows）
  自己实现

**决定**：**B**（辅助线程 + condvar）。

**理由**：
- A 多一个 crate 依赖，wait-timeout 维护活跃度近年下降
- C 跨平台分支 + 复杂度高
- B 用 stdlib 即可：spawn worker thread sleep + kill，主线程 `wait()`；
  wait 返回时通知 worker 退出
- 实现 ~30 行 Rust，**不增加外部依赖**

```rust
fn wait_with_timeout(mut child: Child, timeout: Duration)
    -> Result<(ExitStatus, bool /* timed_out */), io::Error>
{
    let (tx, rx) = mpsc::channel();
    let pid_for_kill = child.id();
    let killer = thread::spawn(move || {
        if rx.recv_timeout(timeout).is_err() {
            // Wait did not finish in time; signal kill.
            // (Use child handle clone via raw fd; or signal main thread to kill.)
        }
    });
    let status = child.wait()?;
    let _ = tx.send(());
    let _ = killer.join();
    Ok((status, /* timed_out flag */))
}
```

（细节 IMPL 时打磨；上述只示意框架。）

### Decision 7: UTF-8 解码 lossy by default

**问题**：子进程 stdout 不是 UTF-8 时怎么办？

**决定**：**lossy decode 到 `string` （无效字节用 U+FFFD 替换）**；同时暴露
`StdoutBytes: byte[]` 给原字节。

**理由**：
- 99% 的脚本场景是文本 → 默认 lossy `string` 让代码简洁
- 强制 strict UTF-8 → 第一个吐 `\xe9` 的 git log 就让整个脚本 panic，反人类
- 用户要 strict 自己拿 bytes 后 `String.FromUtf8Strict` 解（独立 spec）
- 与 Rust `String::from_utf8_lossy` 同语义

## Implementation Notes

### z42 facade API 全貌

```z42
namespace Std.IO;

public sealed class Stdio {
    public static readonly Stdio Inherit = new Stdio(0, null);
    public static readonly Stdio Pipe    = new Stdio(1, null);
    public static readonly Stdio Null    = new Stdio(2, null);
    public static Stdio ToFile(string path) { return new Stdio(3, path); }

    private int    _mode;          // 0/1/2/3 (Inherit/Pipe/Null/File)
    private string _path;          // null unless mode == File
    private Stdio(int mode, string path) { ... }
    internal int    Mode() { return this._mode; }
    internal string Path() { return this._path; }
}

public class Process {
    private string             _program;
    private List<string>       _args;
    private List<string>       _env_keys;     // adds + overrides
    private List<string>       _env_vals;
    private List<string>       _env_remove;
    private bool               _env_clear;    // true → 从空 env 起步
    private string             _cwd;          // null = 继承
    private Stdio              _stdin  = Stdio.Null;     // Run 默认
    private Stdio              _stdout = Stdio.Pipe;     // Run 默认
    private Stdio              _stderr = Stdio.Pipe;     // Run 默认
    private byte[]             _stdin_bytes;  // null 或一次性数据
    private long               _timeout_ms;   // -1 = 无 timeout

    public Process(string program) { ... }

    public Process Arg(string a)              { this._args.Add(a); return this; }
    public Process Args(string[] xs)          { /* extend */ return this; }
    public Process WorkingDirectory(string d) { this._cwd = d; return this; }
    public Process Env(string k, string v)    { /* add to keys/vals */ return this; }
    public Process EnvRemove(string k)        { this._env_remove.Add(k); return this; }
    public Process ClearEnv()                 { this._env_clear = true; return this; }
    public Process Stdin(Stdio s)             { this._stdin = s; return this; }
    public Process Stdout(Stdio s)            { this._stdout = s; return this; }
    public Process Stderr(Stdio s)            { this._stderr = s; return this; }
    public Process StdinBytes(byte[] b)       { this._stdin_bytes = b; return this; }
    public Process StdinString(string s)      { /* UTF-8 encode → _stdin_bytes */ return this; }
    public Process Timeout(TimeSpan t)        { this._timeout_ms = t.TotalMilliseconds(); return this; }

    public ProcessResult Run() {
        // 验证：stdin Pipe 必须配 stdin_bytes 或用 Spawn (decision 4)
        // 调 builtin __process_run，返回时把 result tuple 解成 ProcessResult
    }
    public ProcessHandle Spawn() {
        // builtin __process_spawn 返回 slot_id
        // 包成 ProcessHandle 实例
    }
}

public class ProcessResult {
    public int    ExitCode;
    public string Stdout;
    public string Stderr;
    public byte[] StdoutBytes;
    public byte[] StderrBytes;

    public ProcessResult EnsureSuccess() {
        if (this.ExitCode != 0) {
            throw new ProcessExitException(this.ExitCode, this.Stderr, ...);
        }
        return this;
    }
}

public class ProcessHandle : Disposable {
    private long _slot;     // VmContext slot id
    private bool _waited;

    public long Pid {
        get { return __process_handle_pid(this._slot); }
    }

    public ProcessResult Wait()    { ... }
    public ProcessResult? TryWait() { ... }
    public void Kill()             { __process_handle_kill(this._slot, false); }
    public void KillForce()        { __process_handle_kill(this._slot, true); }
    public void WriteStdin(byte[] b) { ... }
    public void CloseStdin()         { ... }
    override public void Dispose()   { __process_handle_drop(this._slot); }
}
```

### Rust builtin 列表

```rust
// src/runtime/src/corelib/process.rs
pub fn builtin_process_run(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_spawn(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_wait(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_try_wait(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_kill(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_write_stdin(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_close_stdin(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_pid(ctx: &VmContext, args: &[Value]) -> Result<Value>;
pub fn builtin_process_handle_drop(ctx: &VmContext, args: &[Value]) -> Result<Value>;
```

注册到 `src/runtime/src/corelib/mod.rs` 现有 BUILTINS 表（与 `__file_*` 一组同模式）。

### 异常类型

均继承 `Std.Exception`：

- `Std.ProcessStartException(string program, string osError)`
- `Std.ProcessExitException(int exitCode, string program, string stderrPreview)`
- `Std.ProcessTimeoutException(string program, TimeSpan timeout)`
- `Std.ProcessHandleInvalidException()` — handle 已 dispose 后调方法

放在 `src/libraries/z42.io/src/Exceptions/` 下，每个一个 .z42（与
`InvalidMarshalException` 同布局）。

### 错误映射

| Rust 路径 | z42 异常 |
|---|---|
| `Command::spawn` 返回 `io::Error` kind `NotFound` | `ProcessStartException` |
| `Command::spawn` 返回其他 io::Error | `ProcessStartException`（message 含 io::Error.kind） |
| `wait_with_timeout` 触发超时 → kill 后 | `ProcessTimeoutException` |
| Slot 表查不到 id | `ProcessHandleInvalidException` |

### 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| Windows 上 `Command::new("./build.sh")` 路径解析与 sh 行为不同（CMD vs PowerShell vs WSL） | 跨平台脚本可能在 Windows 上找不到 shebang 脚本 | 文档明示：z42 不解释 shebang；Windows 上要跑 .sh 须显式 `new Process("bash").Arg("./build.sh")` |
| 大量 stdout 输出 deadlock（pipe 满，子进程阻塞） | Run() 可能 hang | builtin 用 `wait_with_output()` 而不是先 `wait` 再读 stdout，stdlib 自动分线程读 |
| 进程泄漏（用户 Spawn 后忘 Wait/Kill） | 子进程僵尸 | `ProcessHandle.Dispose()` 在 GC 时自动调 → `__process_handle_drop` 检测仍 alive → 强制 kill + reap（与 Rust `Child` Drop 行为一致） |
| Timeout 后 kill race（子进程刚好自然退出） | `ProcessTimeoutException` 误抛 | wait_with_timeout 实现里 atomic flag 判断 "actually killed by us" |
| z42.test 在 wasm target 上的 process spawn 失败 | wasm 测试 broken | wasm target 上 `__process_*` builtins 注册为"always throw ProcessStartException with 'not supported on wasm'"，本 spec 单测前 skip wasm |

## Testing Strategy

### Rust 单测（`process_tests.rs`）

- argv 透传（用 `echo` 等 OS 自带工具，对比 stdout 字节）
- exit code 透传（`exit 0` / `exit 1` / `exit 42`）
- env 三种模式（inherit / override / clear+set）
- cwd 切换（spawn `pwd` 验证）
- stdio 四种 mode 组合（Inherit / Pipe / Null / File）
- timeout 触发 + 自然结束两路径
- stdin bytes 一次性 + Spawn 流式
- ProcessStartException：`__process_run` "nonexistent-command-xyz"

### z42 集成测试（`src/libraries/z42.io/tests/process_*.z42`）

每个 spec scenario 一条用例，用 `Std.Test.Assert.Equal` 等断言；通过
`z42c test --workspace` 跑。

### 跨平台

- Linux + macOS CI 自动跑全部
- Windows 暂不在 CI（z42 主线尚未支持 Windows runner，单独 spec 跟进）；
  代码必须能 compile-check（用 `#[cfg(windows)]` 路径在 mod.rs 出现）

## Estimated Effort

约 1.5 天：

- z42 facade 类（4 个 .z42 文件）：0.3 天
- Rust builtins 实现（process.rs + slot table）：0.5 天
- timeout helper（辅助线程 + condvar）：0.2 天
- Rust + z42 测试：0.3 天
- 文档（design + workflow 同步）：0.2 天
