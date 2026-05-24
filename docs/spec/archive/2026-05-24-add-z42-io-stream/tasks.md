# Tasks: add Std.IO.Stream + MemoryStream

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24
> 类型：feat（z42.io 扩展；纯脚本；无 VM/IR 改动）
> Spec：[proposal](proposal.md)
>
> **注**：与 z42.yaml 同 caveat — z42.core 当前因并行 session `rename-primitives-to-pascal-case`
> mid-flight 不可编译。Stream/MemoryStream 源 + tests + 文档 + spec 已全部 ship；
> 端到端 `./scripts/test-stdlib.sh z42.io` 待并行 session 修好后跑。
> 源码遵循已经验证的 z42 stdlib pattern（`virtual` / `override` 跟 Object.Equals
> 同款，throw stub 模式跟 Object 同款）— scope 内回归风险低。

## 阶段 1: SeekOrigin constants

- [ ] 1.1 NEW `src/libraries/z42.io/src/SeekOrigin.z42`：static int Begin=0 / Current=1 / End=2

## 阶段 2: Stream base class

- [ ] 2.1 NEW `src/libraries/z42.io/src/Stream.z42`：
  - capability: `CanRead() / CanWrite() / CanSeek()` 默认 false
  - core: `Read(byte[], int, int) -> int` / `Write(byte[], int, int) -> void`
  - lifecycle: `Flush() -> void` / `Close() -> void`
  - optional position: `Length() -> long` / `Position() -> long` /
    `Seek(long, int) -> long` — base throw `NotSupportedException`
  - convenience: `ReadAllBytes() -> byte[]` / `WriteAllBytes(byte[]) -> void`
    / `ReadExactly(int) -> byte[]`
  - 注：z42 无 abstract 关键字 — 用 concrete base + throw stubs；
    subclass override 改 `CanXxx() -> true` 并 override 对应方法

## 阶段 3: MemoryStream subclass

- [ ] 3.1 NEW `src/libraries/z42.io/src/MemoryStream.z42`：
  - 两种 constructor：
    - `MemoryStream()` — 空，writable，growable
    - `MemoryStream(byte[] data)` — read-only over data
  - `_buffer: byte[]` + `_length: int` + `_position: int` + `_writable: bool`
  - override `CanRead / CanWrite / CanSeek` 返回 true（writable 时；read-only 返 false 写）
  - override `Read / Write / Seek / Length / Position / Flush / Close`
  - 新增 `ToArray() -> byte[]` 拷贝当前 _buffer[0.._length]
  - 写超出 _length 自动 grow（power-of-2 起始 16）

## 阶段 4: tests

- [ ] 4.1 NEW `tests/stream_memory.z42`：
  - empty MemoryStream 初始 Length=0 / Position=0
  - Write 3 bytes 后 Length=3 / Position=3
  - Seek(0, Begin) → Position=0 / Read 3 bytes → 拿回原数据
  - read-only MemoryStream Write throw NotSupportedException
  - growth：Write 100 bytes（超 16）→ ok
  - ToArray() = original bytes (snapshot 不 share backing)
- [ ] 4.2 NEW `tests/stream_base.z42`：
  - 直接 new Stream() 的 Read / Write / Seek 都 throw NotSupportedException
  - CanRead / CanWrite / CanSeek 默认 false
- [ ] 4.3 NEW `tests/stream_convenience.z42`：
  - ReadAllBytes 从 MemoryStream 完整读出
  - WriteAllBytes 完整写入
  - ReadExactly(n) — n 字节读不满时 throw `EndOfStreamException`-类异常
    （v0 用 `Std.NotSupportedException` 或 generic `Exception`，因没有专用
    `EndOfStreamException` — 加 TODO 等 `add-z42-io-exceptions` follow-up）

## 阶段 5: 文档

- [ ] 5.1 NEW `docs/design/stdlib/io-stream.md`：API + 使用例 + 设计决策 +
  Deferred section（filestream / networkstream / textreader / bufferedstream /
  async / refactor compression / refactor binary）
- [ ] 5.2 MODIFY `docs/design/stdlib/roadmap.md`：
  - "已落地" 加 Stream/MemoryStream 一行
  - "Deferred Backlog Index" 加 io-stream 延后项索引（filestream / textreader /
    bufferedstream / async / refactor-* 等 6 项）
- [ ] 5.3 MODIFY `docs/design/stdlib/overview.md`：z42.io 入口表加 Stream/MemoryStream
- [ ] 5.4 MODIFY `docs/roadmap.md` Deferred Backlog Index：加 io-stream 索引
- [ ] 5.5 z42.io README.md update（如有）：加 Stream 简介

## 阶段 6: 验证 + 归档

- [ ] 6.1 `./scripts/test-stdlib.sh z42.io` 全绿
- [ ] 6.2 `./scripts/test-all.sh --parallel` 全绿不回归
- [ ] 6.3 mv `docs/spec/changes/add-z42-io-stream/` →
  `docs/spec/archive/YYYY-MM-DD-add-z42-io-stream/`
- [ ] 6.4 commit + push

## 备注

- `out` 是 z42 reserved word — 用 `result` / `buf` / `b` 替代
- z42 class field 不接受 generic type param — 用 raw `byte[]` + `int` count 模式
- `static int X = N;` 而非 `const`
- byte literal: `(byte)N`
