# Proposal: full-streaming Process stdin/stdout/stderr as Std.IO.Stream

## Why

`Std.IO.ProcessHandle` already supports `WriteStdin(byte[])` / `CloseStdin()`
on the write side, and `Wait()` returns a `ProcessResult` whose
`StdoutBytes` / `StderrBytes` give the entire buffered child output. That
shape is fine for short-lived commands but breaks down for:

- **Long-running children with continuous output** (`tail -f`, log
  followers, build watchers): caller must wait until the child exits
  before seeing any bytes — defeats the purpose
- **Large outputs** that don't fit in memory: today's accumulator pins
  every byte until `Wait()`; streaming reads let the consumer
  process-and-drop chunks
- **Pipeline composition** with `Std.IO.Stream` siblings (`FileStream`,
  `MemoryStream`, `CompressionEncoderStream`, future `NetworkStream`):
  user can today wrap `result.StdoutBytes` in `new MemoryStream(...)`
  *after* the fact, but can't tee a child's live output through a
  pipeline

This spec adds true streaming reads on the Native side (2 new
`__process_handle_read_*` builtins) and exposes z42-side `Stream`
wrappers so child stdio is a first-class citizen of the
`Std.IO.Stream` ecosystem.

## What Changes

- Add 2 new corelib builtins (`__process_handle_read_stdout` /
  `__process_handle_read_stderr`) that consume from the live child
  pipe one buffer at a time, blocking until bytes arrive / EOF /
  error. Same buffer-fill shape as `__file_read`
  (`slot, buf, offset, count -> int`); returns `0` on EOF
- New z42 class `Std.IO.ProcessStdinStream : Stream` — write-only
  wrapper around `WriteStdin` + `CloseStdin`. `Close()` closes the
  child's stdin (so it sees EOF) but leaves the handle live
- New z42 class `Std.IO.ProcessOutputStream : Stream` — read-only
  wrapper around the new read builtins. Parameterised by which FD
  (1 = stdout, 2 = stderr) so a single class serves both
- New `ProcessHandle.GetStdinStream() / GetStdoutStream() /
  GetStderrStream()` accessors (cached on the handle so repeated
  calls return the same Stream)
- `ProcessHandle.Wait()` semantics unchanged but explicitly
  documented: if the user has already drained stdout/stderr via
  streaming reads, `result.StdoutBytes` / `result.StderrBytes`
  reflect only whatever was left in the pipe when `Wait()` ran
  (typically empty)
- Native side: `drain_readers` is unchanged. `slot.stdout_reader` /
  `slot.stderr_reader` become readable in-place via the new builtins
  before being consumed by `Wait()`

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/process.rs` | MODIFY | 加 2 个 read builtin（`builtin_process_handle_read_stdout` / `builtin_process_handle_read_stderr`）；`drain_readers` 不变；用与 `__file_read` 一致的 buffer-fill shape |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 末尾 append 2 个 builtin entry（preserve existing BuiltinId stability） |
| `src/libraries/z42.io/src/ProcessStdinStream.z42` | NEW | write-only Stream subclass，delegate WriteStdin / CloseStdin |
| `src/libraries/z42.io/src/ProcessOutputStream.z42` | NEW | read-only Stream subclass，parameterised by fd（1 = stdout / 2 = stderr） |
| `src/libraries/z42.io/src/ProcessHandle.z42` | MODIFY | 加 `GetStdinStream / GetStdoutStream / GetStderrStream` + 缓存字段；ProcessHandleNative 加 2 个 [Native] binding |
| `src/libraries/z42.io/tests/process_stream_stdio.z42` | NEW | streaming write / streaming read / pipeline 组合 / close idempotence |
| `docs/design/stdlib/io-stream.md` | MODIFY | 加 `process-stream-stdio` Landed 项；composability 例子里把 Process 接进来 |
| `docs/design/stdlib/roadmap.md` | MODIFY | Std.IO.Stream 延后项索引加一行（landed） |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 同步 |
| `docs/spec/changes/add-process-stream-stdio/proposal.md` | NEW | 本文件 |
| `docs/spec/changes/add-process-stream-stdio/tasks.md` | NEW | 实施清单 |

**只读引用**：

- `src/runtime/src/corelib/fs.rs` — buffer-fill shape 参考（`__file_read`）
- `src/libraries/z42.io/src/Stream.z42` — base class 协议
- `src/libraries/z42.io/src/FileStream.z42` — Stream subclass 形态参考
- `src/libraries/z42.io/src/ProcessResult.z42` — Wait 结果形态
- `src/runtime/src/vm_context.rs` — `with_process_slot` / `take_process_slot` 现有 API

## Out of Scope

- 不改 `Process.Run()` (one-shot) — 这条路径仍 buffer 全部 stdio；streaming
  只有 `Process.Spawn()` 路径（ProcessHandle）才用得上
- 不加 async / 非阻塞 read variant — 阻塞 read 与 z42 当前同步 IO 模型
  一致（gated on L3 async/await）
- 不改 `drain_readers` 行为 — Wait 仍 drain 剩余 pipe bytes，保持向后
  兼容（pre-1.0 但调用方明显期望 "Wait 后 ProcessResult 是 self-contained"）
- 不引入 `CopyTo(Stream)` helper — 等独立 spec
- 不引入 timeout-on-read — 复用 process-level `Timeout(ms)`；single-read
  timeout 是 deferred

## Open Questions

无 — 设计与 FileStream / MemoryStream / CompressionStream 100% 一致。
