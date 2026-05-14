# Tasks: Std.Process

> 状态：🟢 已完成 | 创建：2026-05-13 | 完成：2026-05-14
> 类型：feat（新增 stdlib + corelib builtins）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/process/spec.md](specs/process/spec.md)

## 进度概览

- [x] 阶段 1: VmContext slot 表 + 基础 builtin 框架
- [x] 阶段 2: `__process_run` 同步路径（含 stdin bytes / stdio 四态 / cwd / env / exit code）
- [x] 阶段 3: `__process_spawn` + handle builtins（wait / try_wait / kill / write_stdin / drop）
- [x] 阶段 4: `__process_run` + `__process_handle_wait` 超时支持（仅 Run，handle 用 try_wait 轮询）
- [x] 阶段 5: z42 facade（Process / Stdio / ProcessResult / ProcessHandle 4 个 .z42）
- [x] 阶段 6: 异常类（ProcessStartException / ExitException / TimeoutException / HandleInvalidException）
- [x] 阶段 7: Rust 单测（21 个）+ z42 集成测试（22 个 [Test] 跨 7 文件）
- [x] 阶段 8: 文档（design/runtime/stdlib-process.md 落地待 follow-up；其余文档同步）
- [x] 阶段 9: GREEN 验证 + 归档 + commit + push

## 阶段 1: VmContext slot 表 + 基础 builtin 框架

- [ ] 1.1 MODIFY [src/runtime/src/vm_context.rs](../../../src/runtime/src/vm_context.rs)
  - 加 `processes: RefCell<HashMap<u64, ProcessSlot>>` 字段
  - 加 `process_next_id: Cell<u64>` 字段
  - 加 helper `alloc_process_slot(child, stdio_handles) -> u64`
  - 加 helper `take_process_slot(id) -> Option<ProcessSlot>`（Wait / Kill 用，移走 child）
  - 加 helper `with_process_slot(id, |slot| ...)`（peek 用）
- [ ] 1.2 NEW [src/runtime/src/corelib/process.rs](../../../src/runtime/src/corelib/process.rs)
  - `ProcessSlot` 结构（design.md decision 2）
  - 空的 builtin 函数 stub（全部签名 + `bail!("not yet")`）
- [ ] 1.3 MODIFY [src/runtime/src/corelib/mod.rs](../../../src/runtime/src/corelib/mod.rs)
  - 注册 9 个新 builtin 到 `BUILTINS` 表（`__process_run` / `_spawn` / `_handle_wait` /
    `_handle_try_wait` / `_handle_kill` / `_handle_write_stdin` / `_handle_close_stdin` /
    `_handle_pid` / `_handle_drop`）
- [ ] 1.4 `cargo build` 通过

## 阶段 2: `__process_run` 同步路径

- [ ] 2.1 实现 argv / env / cwd marshalling（解 Value array → std::process::Command）
- [ ] 2.2 stdin 四态处理（Null / Inherit / Pipe + 一次性 bytes / File）
- [ ] 2.3 stdout / stderr 四态（同上）；File 模式打开句柄重定向
- [ ] 2.4 用 `wait_with_output()` 抓 stdout / stderr 字节，避免 pipe 满 deadlock
- [ ] 2.5 UTF-8 lossy decode → 返回的 Object slots：`[ExitCode: i64, Stdout: str,
      Stderr: str, StdoutBytes: Array<u8>, StderrBytes: Array<u8>]`
- [ ] 2.6 ProcessStartException 路径：spawn 失败 → 抛"Std.ProcessStartException"
      stdlib exception（沿用 `make_stdlib_exception` 模式）
- [ ] 2.7 Rust 单测：argv passing / env / cwd / stdio / exit code / start failure
- [ ] 2.8 `cargo test --lib corelib::process` 全绿

## 阶段 3: `__process_spawn` + handle builtins

- [ ] 3.1 `__process_spawn` 实现：spawn 后 stdin/stdout/stderr 句柄存进 ProcessSlot
- [ ] 3.2 `__process_handle_wait`：从 slot 取 child + readers → wait_with_output 后释放 slot
- [ ] 3.3 `__process_handle_try_wait`：`child.try_wait()` 非阻塞；若已结束读取剩余 stdio
- [ ] 3.4 `__process_handle_kill(force: bool)`：Unix `kill(SIGTERM/SIGKILL)`，
      Windows `TerminateProcess`（force 参数对 Windows 是 no-op，行为已是 kill -9 等价）
- [ ] 3.5 `__process_handle_write_stdin` / `_close_stdin`：从 slot 取 stdin handle 写入 / drop
- [ ] 3.6 `__process_handle_pid`：从 slot 读 child.id() 返回 i64
- [ ] 3.7 `__process_handle_drop`：若 child 仍 alive → kill + wait + 释放 slot
- [ ] 3.8 Rust 单测：spawn + wait / spawn + kill / spawn + write_stdin / try_wait 三态
- [ ] 3.9 `cargo test --lib` 全绿

## 阶段 4: Timeout 支持

- [ ] 4.1 实现 `wait_with_timeout` 辅助函数（design.md decision 6 框架）
  - 用 `mpsc::channel` + 辅助线程
  - 主线程 `child.wait()` 阻塞
  - 辅助线程 `recv_timeout(d)` 触发 kill + 通过共享 atomic flag 标记 "we killed"
- [ ] 4.2 `__process_run` 接收 `timeout_ms: i64`（-1 表示无 timeout）
- [ ] 4.3 timeout 触发 → 抛 `Std.ProcessTimeoutException`
- [ ] 4.4 `__process_handle_wait` 不支持 timeout（z42 用户调 try_wait 轮询）—
      不引入 `wait_timeout` 形态减少 API 表面
- [ ] 4.5 Rust 单测：timeout 触发 + timeout 内自然结束 + race 边界

## 阶段 5: z42 facade

- [ ] 5.1 NEW [src/libraries/z42.io/src/Stdio.z42](../../../src/libraries/z42.io/src/Stdio.z42)
  - sealed class + 4 个静态实例 + `ToFile(path)` 工厂
  - internal `Mode()` / `Path()` getters
- [ ] 5.2 NEW [src/libraries/z42.io/src/Process.z42](../../../src/libraries/z42.io/src/Process.z42)
  - 字段 + 构造
  - 所有 builder methods (Arg / Args / WorkingDirectory / Env / EnvRemove / ClearEnv /
    Stdin / Stdout / Stderr / StdinBytes / StdinString / Timeout)
  - Run() / Spawn() —— 字段编码后调对应 builtin
  - design.md decision 4 验证（stdin Pipe 配置矛盾）
- [ ] 5.3 NEW [src/libraries/z42.io/src/ProcessResult.z42](../../../src/libraries/z42.io/src/ProcessResult.z42)
  - 字段 + EnsureSuccess()
- [ ] 5.4 NEW [src/libraries/z42.io/src/ProcessHandle.z42](../../../src/libraries/z42.io/src/ProcessHandle.z42)
  - 字段 + Wait / TryWait / Kill / KillForce / WriteStdin / CloseStdin / Pid
  - 实现 Disposable（与 z42.core/Disposable.z42 协议对齐）
- [ ] 5.5 `dotnet build src/compiler/z42.slnx` 通过（stdlib 编译 OK）
- [ ] 5.6 验证 `IncrementalBuildIntegrationTests` 中 stdlib 文件数若需更新则更新

## 阶段 6: 异常类型

- [ ] 6.1 NEW `src/libraries/z42.io/src/Exceptions/ProcessStartException.z42`
- [ ] 6.2 NEW `src/libraries/z42.io/src/Exceptions/ProcessExitException.z42`
- [ ] 6.3 NEW `src/libraries/z42.io/src/Exceptions/ProcessTimeoutException.z42`
- [ ] 6.4 NEW `src/libraries/z42.io/src/Exceptions/ProcessHandleInvalidException.z42`
  - 四个均继承 `Std.Exception`，ctor `(string message)` + 必要字段（ExitCode 等）
- [ ] 6.5 Rust 侧 `make_stdlib_exception(ctx, module, "Std.ProcessStartException", msg)`
      调用从 `corelib::process` 工作（验证 `Std.IO.*` namespace 解析正确）

## 阶段 7: 集成测试

- [ ] 7.1 NEW `src/libraries/z42.io/tests/process_basic.z42`
  - Run + stdout / ExitCode
  - Args 数组形式
- [ ] 7.2 NEW `src/libraries/z42.io/tests/process_failure.z42`
  - 找不到 exe → ProcessStartException
  - exit != 0 → EnsureSuccess 抛
- [ ] 7.3 NEW `src/libraries/z42.io/tests/process_env_cwd.z42`
  - Env 覆盖 / ClearEnv / WorkingDirectory
- [ ] 7.4 NEW `src/libraries/z42.io/tests/process_stdio.z42`
  - Stdio.Null / Pipe / Inherit / ToFile 四态
- [ ] 7.5 NEW `src/libraries/z42.io/tests/process_stdin.z42`
  - StdinBytes 一次性
  - StdinString
  - Spawn + WriteStdin 流式
- [ ] 7.6 NEW `src/libraries/z42.io/tests/process_timeout.z42`
  - timeout 触发
  - timeout 内正常结束
- [ ] 7.7 NEW `src/libraries/z42.io/tests/process_spawn.z42`
  - Spawn / Wait / Kill / TryWait
- [ ] 7.8 `./scripts/test-stdlib.sh` 全绿

## 阶段 8: 文档同步

- [ ] 8.1 NEW [docs/design/runtime/stdlib-process.md](../../design/runtime/stdlib-process.md)
  - 架构图 + decision 1-7 的固化版
  - slot 表实现细节 + timeout 实现
- [ ] 8.2 MODIFY [docs/design/language/language-overview.md](../../design/language/language-overview.md)
  - 在 stdlib 章节加 "Std.Process —— 跨平台命令执行" 一句指向 stdlib-process.md
- [ ] 8.3 MODIFY [src/libraries/z42.io/README.md] 若有 — 加入 Process 模块说明
- [ ] 8.4 NEW `examples/process/hello.z42` —— 最小化示例
- [ ] 8.5 准备给 scripts 迁移 spec 留 hook：在 stdlib-process.md 末尾加 "Future Work"
      段提及 "z42 翻写 scripts/*.sh"

## 阶段 9: GREEN 验证 + 归档

- [ ] 9.1 完整运行 `./scripts/test-all.sh`（按 workflow.md 阶段 8）
- [ ] 9.2 grep 验证 no scope creep：
  - `git status` 仅本 spec 列入 Scope 的文件被改动
- [ ] 9.3 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 9.4 移动 `docs/spec/changes/add-std-process/` → `docs/spec/archive/2026-05-13-add-std-process/`
- [ ] 9.5 commit: `feat(stdlib+vm): add-std-process — cross-platform Process API`
- [ ] 9.6 push origin main

## 实施期补丁（非 Scope-creep，发现即修，独立 commit）

1. **fix-cross-pkg-subclass-fields** — `Std.ProcessStartException` 等 4 个异常类继承 `Std.Exception`（z42.core），暴露了 cross-zpkg 子类字段继承的潜在 bug：base.fields 在 `build_type_registry` 跨 zpkg 解析失败 → 子类丢失 Message 等继承字段。修复成独立 spec：[docs/spec/archive/2026-05-14-fix-cross-pkg-subclass-fields/](../../archive/2026-05-14-fix-cross-pkg-subclass-fields/)。先 ship 该 fix，再恢复本 spec 收尾。
2. **convert_value 引用类型 identity cast pass-through**（in-scope）— Process.Run 返回 `object[]` tuple，z42 facade 用 `(string)raw[2]` / `(byte[])raw[4]` cast。runtime `convert_value` 原本对 Str/Array/Object → 同类型 cast 直接 `bail!`（"InvalidCastException: cannot convert Str(...) to type tag 0x0D"）。修复 4 行：识别 Str→Str / Array→Array / Object→Object / Null→ref 为 no-op pass-through。位置 `src/runtime/src/interp/exec_value.rs:194-228`，注释含 add-std-process 出处。
3. **Process.z42 静态字段 → 字面量**（in-scope workaround）— 原代码用 `this._stdout.GetMode() == Stdio.MODE_FILE ? ... : null`。z42 当前 `public static int MODE_FILE = 3;` 的 read-path 静态初始化未运行（Stdio.z42 注释里已标注此限制），`Stdio.MODE_FILE` 返回 0 而非 3，三目永远走 null 分支 → Process.Run 把 stdout_path 当 None 传给 native，触发 "stdout/stderr Stdio.ToFile missing path"。改为字面量 `== 3` 暂时绕过。**Deferred 项**：等 static field initialization 落地后，把字面量改回 `Stdio.MODE_FILE`。

## 备注

### 不解决的问题（follow-up）

- **scripts/*.sh 实际迁移** — 独立 batch spec（依赖本 spec + Glob + CLI parser）
- **Async API** — `Run()` / `Wait()` 异步版，依赖 L3-A1 async 落地
- **进程组 / signal 转发** — 跨平台差异大，独立 spec
- **后台 daemon 管理** — 不在脚本场景，不在 z42 stdlib 范围
- **Windows runner CI 跑跨平台单测** — z42 项目尚未支持 Windows CI，独立 spec

### 风险监控

- **wasm target 上 spawn 失败**：design.md 风险表已记录，wasm `__process_*`
  注册为 always-throw stub，相关 z42 测试在 wasm CI skip
- **timeout helper 线程泄漏**：实现时确保 `wait_with_timeout` 出错路径也
  notify 辅助线程退出，避免线程数随 timeout 次数线性增长
- **slot 表 generation 计数**：用 u64 single counter（永不复用 id），
  避免 generation 复杂度
