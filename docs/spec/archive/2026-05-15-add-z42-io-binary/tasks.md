# Tasks: add z42.io.binary

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

stdlib roadmap P2 表里的 `z42.io.binary`：`BinaryReader` / `BinaryWriter` over
`byte[]`。低层级二进制流读写，对标 C# `System.IO.BinaryReader/Writer` + Rust
`byteorder` crate。用于协议解析、自定义文件格式、调试 `.zbc` 二进制内容等。

v0 API：byte / int16 / int32 / int64 read+write，LE + BE 两种字节序，外加 `byte[]` /
UTF-8 string helper + cursor 管理（Position / Length / Seek / Skip / EndOfStream）。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 缓冲方式 | (a) 固定 `byte[]` / (b) `Std.IO.Stream` 抽象 | (a) | z42 暂无 Stream trait；byte[] 简单且覆盖 protocol 场景 |
| 2. Reader 失败 | (a) 抛 `EndOfStreamException` / (b) 返回 -1 / null | (a) | fail-fast；C# parity |
| 3. Writer 容量增长 | (a) doubling on overflow / (b) 显式 capacity 必填 | (a) | 默认起始 64 bytes，2x grow；不需要调用方猜 |
| 4. Int16 返回类型 | (a) `int` (sign-extended) / (b) `short` | (a) | `short` 仍在 parallel work，未 HEAD-available |
| 5. Float read/write | (a) 不提供 / (b) 提供 | (a) | 需要 IEEE 754 bit-cast (`BitConverter`)，z42 当前没有；defer |
| 6. UTF-8 string | (a) length-prefixed / (b) 调用方传 byte count | (b) | 不强制 framing；由调用方决定长度来源 |
| 7. 异常类 | (a) 复用 `Std.EndOfStreamException` / (b) 新 `BinaryException` 在包内 | (b) | EndOfStreamException 不存在；新加到 z42.core 会扰动 incremental cache 计数。同 UriException / TomlException 模式：包局部 + `Std` namespace |
| 8. Namespace 层级 | (a) `Std.IO.Binary`（语义最佳）/ (b) `Std.Binary`（top-level）| (b) | 实施期发现编译器对三段 namespace（`Std.X.Y` 形式）下的方法解析失效——unqualified `BinaryWriter` 解析到当前命名空间而非 imported。退到 top-level；package 名仍 `z42.io.binary`（不变更 roadmap 标识）。未来 nested-ns 修好后可改回 `Std.IO.Binary`，详 design doc Deferred |
| 9. WriteByte/ReadByte 入出 | (a) `byte` 参数/返回 / (b) `int` 接口（caller cast）| (b) | 全 stdlib 没有 `byte` 作 method param/return 的先例（首次尝试导致 `undefined function` dispatch failure）。统一使用 `int`，调用方 `(byte)x` 或 `(int)b` cast；待 narrow-int primitives 落地后再评估 |
| 10. Int32 sign-extend | (a) 不处理（i64 backing 直接 OR） / (b) bit-31 检查后减 2^32 | (b) | 不 sign-extend 时负值变正大数；ReadInt32 测试 `Assert.Equal(-2147483647, ReadInt32LE())` 直接失败。Read 必须显式 sign-extend；Write 一侧靠 z42 `>>` 是 arithmetic shift 自然保留符号位 |

## 阶段 1: 包骨架

- [x] 1.1 NEW `src/libraries/z42.io.binary/z42.io.binary.z42.toml` — manifest（dep on z42.core + z42.encoding for Utf8）
- [x] 1.2 NEW `src/libraries/z42.io.binary/src/BinaryReader.z42`
  - `namespace Std.IO.Binary;`
  - 字段：`byte[] _data`, `int _pos`
  - Position / Length / EndOfStream() / Seek(int) / Skip(int)
  - ReadByte() → byte
  - ReadInt16LE() / ReadInt16BE() → int (sign-extended)
  - ReadInt32LE() / ReadInt32BE() → int
  - ReadInt64LE() / ReadInt64BE() → long
  - ReadBytes(int count) → byte[]
  - ReadString(int byteCount) → string (UTF-8)
- [x] 1.3 NEW `src/libraries/z42.io.binary/src/BinaryWriter.z42`
  - `namespace Std.IO.Binary;`
  - 字段：`byte[] _buffer`, `int _pos`
  - Length / ToArray() / Clear()
  - WriteByte(byte) / WriteInt16LE(int) / WriteInt16BE / WriteInt32LE/BE / WriteInt64LE/BE
  - WriteBytes(byte[]) / WriteString(string) (UTF-8)
  - 内部 _ensureCapacity 自动 2x grow

## 阶段 2: 测试

- [x] 2.1 NEW `tests/binary_basic.z42` — round-trip int16/32/64 LE+BE，cursor 推进，EndOfStream
- [x] 2.2 NEW `tests/binary_errors.z42` — 越界 read / write 后 ToArray 一致性
- [x] 2.3 NEW `tests/binary_strings.z42` — UTF-8 bytes/string round-trip + multi-byte

## 阶段 3: Wiring + docs

- [x] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.io.binary"`
- [x] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.IO.Binary`
- [x] 3.3 NEW `src/libraries/z42.io.binary/README.md`
- [x] 3.4 NEW `docs/design/stdlib/io-binary.md`
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/build-stdlib.sh` 全绿
- [x] 4.2 `./scripts/test-stdlib.sh z42.io.binary` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.4 mv → `docs/spec/archive/2026-05-15-add-z42-io-binary/`
- [x] 4.5 commit + push

## 实施期发现

1. **Compiler bug: 三段 namespace 方法解析失效**（最关键）。`namespace Std.IO.Binary;`
   下的类，从外部 `using Std.IO.Binary;` 然后 `new BinaryWriter()` → VM 报
   `undefined function SmokeBin.BinaryWriter.WriteByte`（错误地解析到调用方
   namespace）。同源文件改 `namespace Std.Binary;`（两段）立即工作。stdlib
   里此前所有 namespace 都是 `Std.<single>` 形式，z42.io.binary 是首个三段
   尝试。变通：本期用 `Std.Binary`；package 名仍 `z42.io.binary`（保留 roadmap
   标识）。Deferred ID `io-binary-future-nested-ns`：等编译器 fix 后改回。
2. **`byte` 作 method param/return type 全 stdlib 无先例**。最初设计
   `WriteByte(byte b)` / `ReadByte() → byte`，VM dispatch 返回
   `undefined function`。改成 `WriteByte(int)` / `ReadByte() → int`（low-8
   写入；read 返回 0-255 int）。调用方按需 `(byte)x` cast，与 z42.encoding
   convention 一致。
3. **Int32 read sign-extension 必须显式**。z42 `int` 实际是 i64 backed；写时
   `>>` 是 arithmetic shift 自然保留符号；读时把 byte[3] OR 入位 24-31 得到
   正大数（如 -1 → 4294967295），与 `int` 语义不符。Solution：bit-31 检查后
   `raw - 0x100000000L`。Int64 不需要因为 `(long)(int)x` 路径里的 i64 overflow
   自然给出正确值。
4. **Parser overflow on `-9223372036854775808L`**：先 parse `9223372036854775808`
   为 i64 字面量（i64::MAX+1）→ overflow。同 C# / Java 缺陷。本期测试用 i64::MIN+1
   规避；Backlog 候选：parser 支持 unary minus 在字面量边界判定（unary 上下文
   把 `-` 当作字面量符号）。
