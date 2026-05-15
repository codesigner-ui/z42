# z42.io.binary — Low-level binary stream over byte[]

> 落地版本：2026-05-15（add-z42-io-binary）
> 包路径：`src/libraries/z42.io.binary/`
> 命名空间：`Std.Binary`（顶层 — 三段 namespace 待 compiler fix；详 Deferred）

## 职责

`BinaryReader` / `BinaryWriter` 提供 byte / int16 / int32 / int64 显式字节序
（LE + BE）读写 + UTF-8 string + 原始 byte[] helper + cursor 管理。

**对标**：C# `System.IO.BinaryReader/Writer` + Rust `byteorder` crate。

**适用场景**：自定义二进制协议、二进制文件格式、`.zbc` introspection、跨进程
binary marshal、CTF / forensics 工具。

## API surface

```z42
class BinaryReader {
    BinaryReader(byte[] data)
    int  GetPosition() / GetLength()
    bool EndOfStream()
    void Seek(int pos) / Skip(int count)
    int  ReadByte()                       // 0..255
    int  ReadInt16LE() / ReadInt16BE()    // sign-extended
    int  ReadInt32LE() / ReadInt32BE()    // sign-extended
    long ReadInt64LE() / ReadInt64BE()
    byte[] ReadBytes(int count)
    string ReadString(int byteCount)      // UTF-8
}

class BinaryWriter {
    BinaryWriter() / BinaryWriter(int initialCapacity)
    int  GetLength()
    void Clear()
    byte[] ToArray()                      // snapshot copy
    void WriteByte(int b)                 // low 8 bits
    void WriteBytes(byte[] data)
    void WriteInt16LE(int v) / WriteInt16BE(int v)
    void WriteInt32LE(int v) / WriteInt32BE(int v)
    void WriteInt64LE(long v) / WriteInt64BE(long v)
    int  WriteString(string s)            // returns bytes written
}

class BinaryException : Exception     // in Std namespace
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 缓冲方式 | byte[] / Stream 抽象 | byte[] | z42 暂无 Stream trait；byte[] 简单且覆盖 protocol 场景 |
| 2. Reader 失败 | exception / -1 sentinel | exception | fail-fast；C# parity |
| 3. Writer grow | 2x doubling / 固定 | 2x doubling | 均摊 O(1)；初始 64 bytes |
| 4. Int16 返回 | int (sign-ext) / short | int | `short` 不在 HEAD-available 的 primitive 集 |
| 5. Float read/write | 提供 / 不提供 | 不提供 | 需 BitConverter intrinsic；defer |
| 6. UTF-8 string framing | length-prefixed / explicit byteCount | explicit | 不强制 framing 协议；caller 决定 |
| 7. 异常类 | 复用 Std.EndOfStreamException / 包内 BinaryException | 包内 | EndOfStream 不存在；新加到 z42.core 会扰动 incremental cache 计数 |
| 8. Namespace 层级 | `Std.IO.Binary` / `Std.Binary` | `Std.Binary` | 实施期发现编译器对三段 namespace 方法解析失效；package 名仍 `z42.io.binary` |
| 9. Byte param/return | `byte` 类型 / `int` | `int` | stdlib 无 `byte` 方法签名先例；统一 int + caller cast |
| 10. Int32 sign-extend | 不处理 / bit-31 显式 | 显式 | 不处理时负值变正大数 |

## 实现结构

```
src/BinaryReader.z42  (~140 行)
└── class BinaryReader  — cursor + read helpers

src/BinaryWriter.z42  (~155 行)
└── class BinaryWriter  — append-only buffer with 2x grow

src/BinaryException.z42  (~13 行)
└── class BinaryException : Exception  in Std namespace
```

## 字节序约定

- **LE (Little-Endian)** 后缀：低字节存低地址。x86/ARM 原生格式，protobuf、QUIC、
  大多数文件格式（PNG IDAT / ZIP / msgpack）。
- **BE (Big-Endian)** 后缀：高字节存低地址（"network byte order"）。TCP/UDP
  header、IP address、DNS、ssh wire format、Java class file。

没有 default — 显式后缀强制调用方阅读时知道字节序。

## Int32 sign-extension 注解

z42 `int` 物理上 i64-backed。直接 `b0 | (b1<<8) | (b2<<16) | (b3<<24)` 在
i64 算术下保留高位为 0，使 `-1` (0xFFFFFFFF bytes) 读回成 4294967295。

Solution：
```z42
long raw = (long)b0 | ((long)b1<<8) | ((long)b2<<16) | ((long)b3<<24);
if ((raw & 2147483648L) != 0L) { raw = raw - 4294967296L; }
return (int)raw;
```

Int64 read 不需要因为 `(long)int << 32` 路径里的 i64 overflow 自然给出正确
符号位（i64::MIN 来自 `1L << 63`）。

## 不支持（Deferred）

### io-binary-future-nested-ns

- **来源**：理想 namespace 是 `Std.IO.Binary`（与 `Std.IO` 组成树）
- **触发原因**：编译器 method-resolution 对 `using Std.X.Y;` + unqualified
  identifier reference 不工作；首次尝试 `Std.IO.Binary` 时 VM 报 `undefined
  function <caller-ns>.BinaryWriter.WriteByte`（误解析到调用方 namespace）
- **前置依赖**：compiler `TypeChecker` / `OverloadResolver` 支持多段 namespace
  imports（C# / Java 都正常支持）
- **触发条件**：fix-nested-namespace-resolution spec 完成
- **当前 workaround**：用 `Std.Binary`（两段）；index.json 映射 `Std.Binary` →
  `z42.io.binary.zpkg`；package 名保持 `z42.io.binary` 不变

### io-binary-future-float-double

- **来源**：`WriteFloat32 / Float64 / ReadFloat32 / Float64`（IEEE 754 编码）
- **触发原因**：需 `BitConverter.Int32BitsToFloat` / `DoubleToInt64Bits` 这种
  bit-level 转换；z42 当前没有；不能纯脚本实现（需 VM intrinsic）
- **触发条件**：z42.core 或 z42.math 落地 `BitConverter` API（独立 spec）
- **当前 workaround**：调用方手算 IEEE 754（极少 用户场景）

### io-binary-future-stream

- **来源**：流式 IO（File / Network 适配），不一次性 load 到 byte[]
- **触发原因**：需要 `Std.IO.Stream` trait + ReadAsync 等；架构性
- **前置依赖**：z42.io 重构出 Stream 抽象 + async / await（L3）
- **当前 workaround**：调用方自己分块 read 到 byte[] + 反复 new BinaryReader

### io-binary-future-varint

- **来源**：7-bit varint 编码（protobuf / Thrift / Avro），小整数 1 字节
- **触发原因**：v0 优先覆盖 fixed-width；varint 是独立特性
- **当前 workaround**：调用方手动 encode

### io-binary-future-byte-typed-api

- **来源**：理想 `WriteByte(byte b)` / `ReadByte() → byte` 类型签名
- **触发原因**：stdlib 当前无 `byte` 作 method param/return 先例；首次尝试
  导致 VM dispatch failure
- **前置依赖**：narrow-int primitives 完整落地（i8/u8 等 first-class）
- **当前 workaround**：用 `int` + caller `(byte)x` cast

### io-binary-future-parser-min-int

- **来源**：`-9223372036854775808L`（i64::MIN）字面量解析 overflow
- **触发原因**：parser 先 read `9223372036854775808` 为 i64 字面量
  （i64::MAX+1）→ Int64.Parse overflow；unary minus 是后置应用
- **当前 workaround**：用 i64::MIN+1（差 1，对边界测试不致命）或
  `-9223372036854775807L - 1L`

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型 + Exception）
- 依赖 `z42.encoding`（Utf8.GetBytes / GetString）
- 与 `z42.json` / `z42.toml` 互补：JSON/TOML 是文本格式，z42.io.binary 是
  二进制格式
- 未来 `z42.net` 接收 TCP/UDP buffer 后用 BinaryReader 解码 header

## 实施期发现

详 archived tasks.md "实施期发现" 段。关键四条：

1. 三段 namespace 编译器 bug（导致 `Std.Binary` workaround）
2. `byte` 不能作 method param/return（导致 `int` API）
3. Int32 read 必须显式 sign-extend（i64 backing 不自动 sign-extend）
4. `-i64::MIN` literal parser overflow
