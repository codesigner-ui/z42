# z42.io.binary

## 职责
低层级二进制流读写 over `byte[]`：`BinaryReader` / `BinaryWriter`，LE / BE
字节序两种显式后缀，外加 UTF-8 string + 原始 `byte[]` helper。用于协议解析、
自定义文件格式、调试 `.zbc` 二进制内容等。

> **包名 vs 命名空间不对齐**：package 是 `z42.io.binary`（沿用 roadmap 标识），
> namespace 是 `Std.Binary`（顶层）。当前编译器对 `Std.X.Y` 三段 namespace 的
> 方法解析不工作；待 nested-ns 修好后改回 `Std.IO.Binary`，详 `docs/design/stdlib/io-binary.md` Deferred。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/BinaryReader.z42`    | 读取 byte / int16 / int32 / int64 (LE+BE) / bytes / UTF-8 string |
| `src/BinaryWriter.z42`    | 写入对称 API + 内部 byte[] 自动 2x grow |
| `src/BinaryException.z42` | 越界 / 非法参数错误（在 `Std` namespace） |

## 入口点

```z42
using Std.Binary;

// Reader
var r = new BinaryReader(bytes);
r.GetPosition() / GetLength() / EndOfStream() / Seek(int) / Skip(int)
r.ReadByte()                 → int    // 0..255
r.ReadInt16LE() / ReadInt16BE() → int  // sign-extended from 16 bits
r.ReadInt32LE() / ReadInt32BE() → int  // sign-extended from 32 bits
r.ReadInt64LE() / ReadInt64BE() → long
r.ReadBytes(int count)       → byte[]
r.ReadString(int byteCount)  → string  // UTF-8

// Writer
var w = new BinaryWriter();              // internal growable MemoryStream
var w = BinaryWriter.OverStream(stream); // wrap any Std.IO.Stream
// Reader: `new BinaryReader(byte[])` for in-memory bytes,
//         `BinaryReader.OverStream(stream)` for any Std.IO.Stream
w.GetLength() / Clear() / ToArray()
w.WriteByte(int b)                       // low 8 bits
w.WriteBytes(byte[] data)
w.WriteInt16LE(int) / WriteInt16BE(int)
w.WriteInt32LE(int) / WriteInt32BE(int)
w.WriteInt64LE(long) / WriteInt64BE(long)
w.WriteString(string s)        → int     // returns bytes written
```

## 用法

```z42
using Std.Binary;
using Std.IO;

// 构造一段 TCP 头（u16 LE port + u32 BE addr）：
var w = new BinaryWriter();
w.WriteInt16LE(8443);
w.WriteInt32BE(0x7F000001);   // 127.0.0.1
byte[] header = w.ToArray();
Console.WriteLine("header len = " + header.Length.ToString());  // 6

// 解码：
var r = new BinaryReader(header);
int port = r.ReadInt16LE();
int addr = r.ReadInt32BE();

// Length-prefixed string framing：
var msg = new BinaryWriter();
string body = "hello z42";
msg.WriteInt16LE(body.Length);   // ASCII so char count == byte count
msg.WriteString(body);

var dec = new BinaryReader(msg.ToArray());
int len = dec.ReadInt16LE();
string s = dec.ReadString(len);
```

## 依赖关系
依赖 `z42.core`（基础类型 + Exception）+ `z42.encoding`（`Utf8.GetBytes` /
`Utf8.GetString` 用于 `WriteString` / `ReadString`）。

## 设计要点

- **byte 参数 / 返回都走 `int`**：z42 stdlib 暂无 `byte` 作 method param/return
  的先例（首次尝试导致 VM `undefined function` 错误）。改用 `int`，调用方按需
  `(byte)x` cast。待 narrow-int primitives 落地后再评估
- **Int32 read 显式 sign-extend**：z42 `int` 是 i64 backed；不 sign-extend 时
  `-1` 读回来变 `4294967295`。bit-31 检查后 `raw - 0x100000000L`
- **Int64 read 不需 sign-extend**：`(long)int << 32` 路径里的 i64 overflow 自然
  给出正确符号
- **Writer 起始容量 64 + 2x grow**：均摊 O(1) 写入；调用方拿 `ToArray()` 取 N-byte 副本
- **ToArray 是 snapshot**：后续 Write 不会影响已返回的数组

## 不在本期 Scope（见 `docs/design/stdlib/io-binary.md` Deferred）

- Float / Double bit-level 读写（需 BitConverter intrinsic）
- `Stream` 抽象（流式 IO，多 source 适配）
- Length-prefixed string framing helper（caller 当前自行 u16/u32 + ReadString）
- 7-bit varint 编码（protobuf 风格）
- 真正的 `byte` 参数 / 返回 API（等 narrow-int primitives）
