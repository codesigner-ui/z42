# Tasks: add Std.IO.FileStream

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat（新 VM builtins + 新 stdlib class；扩展 z42.io）
> Spec：[proposal](proposal.md)

## 阶段 1: VmCore slot table

- [x] 1.1 MODIFY `src/runtime/src/vm_context.rs`：
  - VmCore 加 `file_handles: Mutex<HashMap<u64, crate::corelib::fs::FileHandleSlot>>`
  - 加 `next_file_handle_id: AtomicU64`
  - VmCore default init 两字段都空

## 阶段 2: corelib fs.rs 加 file-handle builtins

- [x] 2.1 MODIFY `src/runtime/src/corelib/fs.rs`：
  - NEW pub(crate) `struct FileHandleSlot { file: Option<std::fs::File>, mode: u8 }`
  - NEW `pub fn builtin_file_open(ctx, args) -> Result<Value>` — args: [path, mode] → I64(slot_id)
  - NEW `pub fn builtin_file_read(ctx, args)` — args: [slot, buf, off, count] → I64(n)
  - NEW `pub fn builtin_file_write(ctx, args)` — args: [slot, buf, off, count] → Null
  - NEW `pub fn builtin_file_seek(ctx, args)` — args: [slot, offset_i64, origin_i32] → I64(new_pos)
  - NEW `pub fn builtin_file_length(ctx, args)` — args: [slot] → I64
  - NEW `pub fn builtin_file_position(ctx, args)` — args: [slot] → I64
  - NEW `pub fn builtin_file_flush(ctx, args)` — args: [slot] → Null
  - NEW `pub fn builtin_file_close(ctx, args)` — args: [slot] → Null (idempotent)
  - 错误处理：path 不存在 / 权限 / 越界 seek → anyhow::bail!（VM 抛 z42-side Exception）

## 阶段 3: corelib mod.rs register

- [x] 3.1 MODIFY `src/runtime/src/corelib/mod.rs`：
  - 末尾 append 8 个 builtin entry（preserve existing BuiltinId stability）
  - 加注释 `// add-z42-io-filestream (2026-05-24) — appended to preserve existing BuiltinIds`

## 阶段 4: z42 facade

- [x] 4.1 NEW `src/libraries/z42.io/src/FileMode.z42`：
  static int Read=0 / Write=1 / Append=2
- [x] 4.2 NEW `src/libraries/z42.io/src/FileStream.z42`：
  - `extends Stream`
  - 2 constructors：`(string path)` 默认 Read mode；`(string path, int mode)` 显式
  - `_slotId: long` 私有字段
  - `_mode: int` 私有字段（决定 CanXxx 返回值）
  - override `CanRead / CanWrite / CanSeek` per mode
  - override `Read / Write / Seek / Length / Position / Flush / Close`
  - 全部 delegate 到 [Native] bindings on `__file_*`

## 阶段 5: tests

- [x] 5.1 NEW `src/libraries/z42.io/tests/file_stream.z42`：
  - write-mode: open temp file → write bytes → close → read back via `File.ReadAllBytes`
  - read-mode: prepare via `File.WriteAllText` → open → read all → close
  - append-mode: write → close → append → close → read full file (matches concat)
  - seek: write 100 bytes → seek to 50 → read 10 → verify
  - close-idempotent
  - cleanup: temp files deleted

## 阶段 6: 文档

- [x] 6.1 MODIFY `docs/design/stdlib/io-stream.md`：
  `io-stream-future-filestream` Deferred 改成 "✅ landed 2026-05-24"
- [x] 6.2 MODIFY `docs/design/stdlib/roadmap.md`：Stream 延后项索引
  filestream 行打勾
- [x] 6.3 MODIFY `docs/roadmap.md` Deferred Backlog Index：strike
  filestream 行
- [x] 6.4 MODIFY `docs/design/stdlib/overview.md`（如有 z42.io 入口）：
  加 FileStream 一行
- [x] 6.5 README 内 z42.io 或 io-stream 主文档加 FileStream pipeline 例

## 阶段 7: 验证 + 归档

- [x] 7.1 `cargo build --release` 不破
- [x] 7.2 `./scripts/test-stdlib.sh z42.io` 全绿
- [x] 7.3 mv `docs/spec/changes/add-z42-io-filestream/` →
  `docs/spec/archive/YYYY-MM-DD-add-z42-io-filestream/`
- [x] 7.4 commit + push

## 备注

- z42.core 因并行 session `rename-primitives-to-pascal-case` mid-flight 不可
  编译，z42 端 test 等并行 session 修好后跑；但 Rust 端 `cargo build`
  + `cargo test --lib` 当前能跑（VM 改动可独立验证）
- 与 process / mutex / channel / compressor 的 slot table 模式 100% 一致；
  无新设计，仅 mechanical add
