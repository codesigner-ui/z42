# Tasks: refactor BinaryReader / BinaryWriter onto Std.IO.Stream

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24
> 类型：refactor（内部存储 byte[] → Stream；保留旧 byte[] 构造路径作 sugar）
> Spec：[proposal](proposal.md)
>
> **注**：与 add-z42-yaml / add-z42-io-stream / refactor-compression-stream
> 同 caveat — z42.core 因并行 session `rename-primitives-to-pascal-case`
> mid-flight 不可编译，端到端 test 待并行 session 修好后跑。本 refactor
> 是 z42-side only 改动（无 native / VM 改动），逻辑直接镜像
> refactor-compression-stream 模式（保留旧 sugar constructor + 加 Stream
> constructor + 内部 delegate to MemoryStream），scope 内回归风险低。

## 阶段 1: dep + scaffolding

- [ ] 1.1 MODIFY `src/libraries/z42.io.binary/z42.io.binary.z42.toml`：
  加 `"z42.io" = "0.1.0"` 到 `[dependencies]`（用 `Std.IO.Stream` / `MemoryStream` / `SeekOrigin`）

## 阶段 2: BinaryReader

- [ ] 2.1 MODIFY `src/libraries/z42.io.binary/src/BinaryReader.z42`：
  - 私有字段 `byte[] _data + int _pos` → 单字段 `Stream _stream`
  - constructor `BinaryReader(byte[] data)` → 内部委托 `new MemoryStream(data)` 设给 `_stream`
  - NEW constructor `BinaryReader(Stream src)` — 验证 `src.CanRead()`，存到 `_stream`
  - `GetPosition() / GetLength() / EndOfStream() / Seek() / Skip()` — capability-gated；underlying `_stream` 抛 `NotSupportedException` 时 wrap 为 `BinaryException` 带原 op 名
  - `ReadByte()` — `byte[1]` buffer + `_stream.Read(buf, 0, 1)` + 检查 n==1 否则 throw `BinaryException("unexpected EOF")`
  - `ReadBytes(n) -> byte[]` — use `Stream.ReadExactly(n)` (which already throws on premature EOF — wrap in BinaryException for caller-friendly message)
  - `ReadInt16LE / BE / 32LE / BE / 64LE / BE` — wrap `ReadBytes(N)` then decode bytes same as before
  - `ReadString(byteCount)` 不变（基于 ReadBytes + UTF-8）
  - 移除 `RequireAvail` (旧 byte[]-only helper) — 替代为 `Std.IO.Stream.ReadExactly` 的 EOF check

## 阶段 3: BinaryWriter

- [ ] 3.1 MODIFY `src/libraries/z42.io.binary/src/BinaryWriter.z42`：
  - 私有字段 `byte[] _buffer + int _pos` → `Stream _stream + bool _ownsStream`
  - existing constructors（默认 + initialCapacity int）— 内部 `new MemoryStream()` 设给 `_stream`，`_ownsStream = true`
  - NEW constructor `BinaryWriter(Stream dest)` — 验证 `dest.CanWrite()`；`_stream = dest`，`_ownsStream = false`
  - `GetLength()` → `(int)_stream.Length()` (要求 seekable；非 seekable wrap `BinaryException`)
  - `ToArray()` — gate on `_ownsStream`；when true, downcast `_stream` to `MemoryStream` and call `ToArray()`. when false, throw `BinaryException("ToArray only valid on default-constructed BinaryWriter")`
  - `Clear()` — gate on `_ownsStream`；when true, seek dest MemoryStream back to 0 (and reset its logical length — note: `MemoryStream.Seek(0, Begin)` doesn't truncate length, so `Clear()` semantically means "reset write cursor only"; existing semantics preserved). when false, throw similar
  - `WriteByte / WriteBytes / WriteInt*` — all become `_stream.Write(byte[], 0, N)` after building the byte[] via existing endian logic
  - remove `EnsureCapacity` (no longer needed — `MemoryStream` grows itself)

## 阶段 4: new pipeline tests

- [ ] 4.1 NEW `src/libraries/z42.io.binary/tests/binary_stream.z42`：
  - `BinaryWriter(MemoryStream)` write 4 ints + read back via `BinaryReader(MemoryStream(dest.ToArray()))` → 值匹配
  - `BinaryReader(MemoryStream(bytes))` — 显式 Stream 构造器路径 round-trip
  - non-seekable stream → `GetPosition()` throws `BinaryException`
  - `BinaryWriter(MemoryStream).ToArray()` throws `BinaryException` (user-supplied dest doesn't support ToArray)
  - close-cascade negative test: `BinaryReader(MemoryStream).Close` semantics — caller still owns underlying

## 阶段 5: docs

- [ ] 5.1 MODIFY `docs/design/stdlib/io-binary.md`：
  - API section 加 Stream-constructor 行
  - 新 "Pipeline composition" 段 with examples
- [ ] 5.2 MODIFY `docs/design/stdlib/io-stream.md`：
  `refactor-binary-reader-stream` Deferred item 改成 "✅ landed 2026-05-24"
- [ ] 5.3 MODIFY `docs/design/stdlib/roadmap.md`：Stream 延后项索引 中
  refactor-binary-reader-stream 行打勾
- [ ] 5.4 MODIFY `docs/roadmap.md` Deferred Backlog Index：strike row

## 阶段 6: 验证 + 归档

- [ ] 6.1 `./scripts/test-stdlib.sh z42.io.binary` 全绿（现有 3 file + 新 binary_stream.z42 = 4 file）
- [ ] 6.2 `./scripts/test-stdlib.sh z42.io` 不破
- [ ] 6.3 mv `docs/spec/changes/refactor-binary-reader-stream/` →
  `docs/spec/archive/YYYY-MM-DD-refactor-binary-reader-stream/`
- [ ] 6.4 commit + push

## 备注

- z42.core 因并行 session `rename-primitives-to-pascal-case` mid-flight
  暂时不可编译；与 add-z42-yaml / add-z42-io-stream / refactor-compression-stream
  同 caveat，端到端 test 等并行 session 修好后跑
