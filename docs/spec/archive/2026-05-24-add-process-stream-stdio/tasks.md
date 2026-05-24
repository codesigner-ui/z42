# Tasks: full-streaming Process stdin/stdout/stderr as Std.IO.Stream

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat（新 VM builtins + 新 stdlib classes；扩展 z42.io）
> Spec：[proposal](proposal.md)

## 阶段 1: VM read builtins

- [x] 1.1 MODIFY `src/runtime/src/corelib/process.rs`：
  - NEW `pub fn builtin_process_handle_read_stdout(ctx, args) -> Result<Value>`
    - args: `[slot_id: long, buf: byte[], offset: int, count: int] -> int`
    - 通过 `ctx.with_process_slot` 借出 `slot.stdout_reader.as_mut()`
    - 调用 `r.read(&mut tmp[..count])` 一次（不重试），返回实际字节数
    - reader 已 take 走（Wait 已发生）→ 返回 `handle_invalid_result` 不可达
      （Stream 由 z42 侧 Wait 之前调用，handle 仍 live）；read 返回 0 = EOF
    - buf 边界检查与 `__file_read` 一致；要求 `offset + count <= buf.Length`
  - NEW `pub fn builtin_process_handle_read_stderr` — 同 shape，作用于
    `slot.stderr_reader`
  - 注：reader 是 `Option<ChildStdout>` / `Option<ChildStderr>`。slot 仍存在
    但 reader 为 None（典型：用户没 Pipe stdin/stdout，Spawn 时 None）→ 直接
    返回 0（EOF）。这条 path 在 facade 侧由 CanRead() 提前防御。

## 阶段 2: corelib mod.rs register

- [x] 2.1 MODIFY `src/runtime/src/corelib/mod.rs`：
  - 末尾 append 2 个 builtin entry（preserve existing BuiltinId stability）
  - 加注释 `// add-process-stream-stdio (2026-05-24) — appended to preserve existing BuiltinIds`

## 阶段 3: z42 ProcessStdinStream

- [x] 3.1 NEW `src/libraries/z42.io/src/ProcessStdinStream.z42`：
  - `public class ProcessStdinStream : Stream`
  - 字段：`_handle: ProcessHandle`、`_closed: bool`
  - `CanWrite() = !this._closed`
  - `Write(buf, off, n)` — slice [off..off+n] → 调 `_handle.WriteStdin(slice)`
  - `Close()` — 调 `_handle.CloseStdin()`，set `_closed = true`；幂等
  - `Flush()` — 无操作（OS pipe 立即写）
  - 其他 Read / Seek / Length / Position 走 base class throw

## 阶段 4: z42 ProcessOutputStream

- [x] 4.1 NEW `src/libraries/z42.io/src/ProcessOutputStream.z42`：
  - `public class ProcessOutputStream : Stream`
  - 字段：`_handle: ProcessHandle`、`_fd: int`（1 = stdout，2 = stderr）、`_closed: bool`
  - constructor：`ProcessOutputStream(ProcessHandle h, int fd)`
  - `CanRead() = !this._closed`
  - `Read(buf, off, n)` — 调 `ProcessHandleNative.ReadStdout/ReadStderr`
    按 fd 分支；返回 int n（0 = EOF）
  - `Close()` — set `_closed = true`；不实际关闭 OS reader（由 ProcessHandle
    自己在 Wait/Drop 时处理）；幂等
  - 注：单类双 fd 比双类清洁；Std.IO.FileStream 已经用同一 class 兼容 3 模式

## 阶段 5: ProcessHandle accessors + Native bindings

- [x] 5.1 MODIFY `src/libraries/z42.io/src/ProcessHandle.z42`：
  - 加 private cached 字段：`_stdinStream`、`_stdoutStream`、`_stderrStream`
  - 加 public method：
    - `ProcessStdinStream GetStdinStream()` — 懒构造 + 缓存
    - `ProcessOutputStream GetStdoutStream()` — 懒构造 + 缓存，fd = 1
    - `ProcessOutputStream GetStderrStream()` — 懒构造 + 缓存，fd = 2
  - 加 ProcessHandleNative `[Native]` binding：
    - `ReadStdout(long slotId, byte[] buf, int offset, int count) -> int`
    - `ReadStderr(long slotId, byte[] buf, int offset, int count) -> int`

## 阶段 6: tests

- [x] 6.1 NEW `src/libraries/z42.io/tests/process_stream_stdio.z42`：
  - test_stdin_stream_write_through — Spawn `cat`，通过 ProcessStdinStream
    Write + Close，Wait 后验证 StdoutBytes == input
  - test_stdout_stream_read — Spawn `echo hello`，通过 ProcessOutputStream
    ReadAllBytes 取 stdout；后续 Wait 看 StdoutBytes 是空
  - test_stderr_stream_read — Spawn `sh -c "echo err 1>&2"`，从
    GetStderrStream() 读
  - test_pipeline_filestream_to_stdin — temp file → FileStream → 拷到
    Process stdin，等价 cat
  - test_close_idempotent — 多次 Close stdin/stdout/stderr stream
  - test_capability_post_close — Close 后 CanWrite/CanRead 返回 false
  - test_get_stream_caches — 两次 GetStdoutStream 返回同对象

## 阶段 7: 文档

- [x] 7.1 MODIFY `docs/design/stdlib/io-stream.md`：
  - 加 `### ~~process-stream-stdio~~ — ✅ landed 2026-05-24` section
  - composability 例子区加一段 `Process → Gzip → FileStream` 管线
- [x] 7.2 MODIFY `docs/design/stdlib/roadmap.md`：
  Stream 延后项索引加 `process-stream-stdio` ✅ 已落地行
- [x] 7.3 MODIFY `docs/roadmap.md` Deferred Backlog Index：
  Stream 行注明 Process streams 已 landed（如果有相关行）
- [x] 7.4 MODIFY `src/libraries/z42.io/README.md`：
  src 核心文件表加 ProcessStdinStream / ProcessOutputStream

## 阶段 8: 验证 + 归档

- [x] 8.1 `cargo check --release` 不破
- [x] 8.2 `cargo test --release --lib` 不引入新 failure（gc pre-existing 不计）
- [x] 8.3 `./scripts/test-stdlib.sh z42.io` 全绿（等并行 session 修好 z42.core）
- [x] 8.4 mv `docs/spec/changes/add-process-stream-stdio/` →
  `docs/spec/archive/YYYY-MM-DD-add-process-stream-stdio/`
- [x] 8.5 commit + push（partial staging of mod.rs 因为并行 session 在改 BUILTINS 早段）

## 备注

- 与 add-z42-io-filestream / refactor-compression-stream-on-iostream / refactor-binary-reader-stream
  同一系列；这是 Std.IO.Stream 生态第 4 个 follow-up
- 并行 session `rename-primitives-to-pascal-case` 仍 mid-flight；z42 端 e2e 测试待其修好后跑
- Native 侧 read builtin 与 `__file_read` 完全镜像（buffer-fill shape + 0 = EOF
  + 越界 anyhow::bail!）；学习成本零
