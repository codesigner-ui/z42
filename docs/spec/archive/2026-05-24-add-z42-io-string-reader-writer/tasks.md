# Tasks: add Std.IO.StringReader + StringWriter

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat（新 stdlib classes；扩展 z42.io；纯脚本，无 VM 改动）
> Spec：[proposal](proposal.md)

## 阶段 1: StringReader

- [x] 1.1 NEW `src/libraries/z42.io/src/StringReader.z42`：
  - field：`_source: string`、`_pos: int`、`_closed: bool`
  - ctor：`StringReader(string source)` — store source ref, pos=0
  - `Peek() -> int` — char at `_pos` or -1 if EOF; throw if closed
  - `Read() -> int` — char at `_pos`, then `_pos++`; -1 if EOF
  - `Read(char[] buf, int offset, int count) -> int` — bulk copy
    `min(count, remaining)` chars; returns actually-copied count
  - `ReadLine() -> string` — scan from `_pos` to next `\n` (or EOF);
    consume trailing `\r\n` or `\n` cleanly; return `null` on
    pre-existing EOF (caller uses `!= null` to terminate)
  - `ReadToEnd() -> string` — `_source.Substring(_pos)`; set pos = length
  - `Close()` — set `_closed = true`; subsequent ops throw
    InvalidOperationException

## 阶段 2: StringWriter

- [x] 2.1 NEW `src/libraries/z42.io/src/StringWriter.z42`：
  - field：`_buf: StringBuilder`、`_closed: bool`
  - ctor 1：`StringWriter()` — empty StringBuilder
  - ctor 2：`StringWriter(int initialCapacity)` — StringBuilder with capacity hint
  - `Write(char c)` — append single char via String.FromChars singleton
  - `Write(string s)` — `_buf.Append(s)`
  - `Write(char[] buf, int offset, int count)` — append slice
  - `WriteLine()` — append `"\n"`
  - `WriteLine(string s)` — `Write(s)` + WriteLine
  - `WriteLine(char c)` — `Write(c)` + WriteLine
  - `ToString() -> string` — `_buf.ToString()`
  - `Clear()` — `_buf.Clear()` (or re-init `_buf = new StringBuilder()`)
  - `Close()` — set `_closed = true`; subsequent Write* throw

## 阶段 3: tests

- [x] 3.1 NEW `src/libraries/z42.io/tests/string_reader.z42`：
  - test_peek_does_not_advance
  - test_read_advances_one_char
  - test_read_at_eof_returns_minus_one
  - test_read_bulk_chars
  - test_read_line_lf
  - test_read_line_crlf
  - test_read_line_no_terminator_at_eof
  - test_read_line_empty_lines
  - test_read_line_eof_returns_null
  - test_read_to_end
  - test_close_then_read_throws
- [x] 3.2 NEW `src/libraries/z42.io/tests/string_writer.z42`：
  - test_empty_initial_state
  - test_write_string
  - test_write_char
  - test_write_chars_bulk
  - test_write_line_empty
  - test_write_line_with_string
  - test_clear_resets
  - test_initial_capacity_does_not_throw
  - test_close_then_write_throws
- [x] 3.3 NEW `src/libraries/z42.io/tests/string_reader_writer_pipeline.z42`：
  - test_writer_to_reader_round_trip — Write lines into StringWriter →
    ToString → StringReader → ReadLine matches input
  - test_lines_from_compressed_string_pseudo —（pure script demo of
    pipeline shape; no compression involved here，跨 spec 留作 docstring）

## 阶段 4: 文档

- [x] 4.1 MODIFY `src/libraries/z42.io/src/Stream.z42`：
  顶部 docstring 加一句 "text I/O is StringReader/StringWriter; future
  StreamReader/Writer bridges char ↔ byte streams with Encoding"
- [x] 4.2 MODIFY `src/libraries/z42.io/README.md`：
  src 核心文件表加 StringReader.z42 + StringWriter.z42
- [x] 4.3 MODIFY `docs/design/stdlib/io-stream.md`：
  加 `add-z42-io-string-reader-writer` ✅ landed 段；deferred section
  add `io-stream-future-streamreader-writer` (encoding-gated)
- [x] 4.4 MODIFY `docs/design/stdlib/roadmap.md`：
  Stream 延后项索引加行 ✅ 已落地 StringReader/Writer

## 阶段 5: 验证 + 归档

- [x] 5.1 `cargo check --release` 不破（应该是 no-op；纯 z42 改）
- [x] 5.2 `./scripts/test-stdlib.sh z42.io` 全绿（新增 ~22 test 全 pass）
- [x] 5.3 mv `docs/spec/changes/add-z42-io-string-reader-writer/` →
  `docs/spec/archive/YYYY-MM-DD-add-z42-io-string-reader-writer/`
- [x] 5.4 commit + push

## 备注

- 与 add-z42-io-stream / add-z42-io-filestream / add-process-stream-stdio /
  refactor-binary-reader-stream / refactor-compression-stream-on-iostream
  同一系列；Stream 生态第 5 个 follow-up
- 纯脚本实现，无 VM 改动；不影响 `cargo` 路径
- `Std.Text.StringBuilder` 已纯脚本化（chunked List<string>），直接用作 backing store
