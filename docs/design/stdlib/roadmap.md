# z42 标准库扩展路线图

> **目的**：列出 z42 当前缺失但中长期必备的标准库包，按重要程度分级，
> 参考 C# BCL 与 Rust std/生态的做法，给后续 spec proposal 提供优先级依据。
>
> **受众**：stdlib 设计者、新包提案者、roadmap reviewer。
>
> **配套文档**：
> - 架构契约：[stdlib.md](overview.md)（三层模型 + `[Native]` 规则）
> - 划分规则：[stdlib-organization.md](organization.md)（包边界 + 层级硬约束）
> - 当前实现状态：[../../src/libraries/README.md](../../src/libraries/README.md)（Extern 现状审计）
>
> **更新策略**：每完成一个新包后，把对应行从本文件移除（迁移到 stdlib-organization.md 现状表）；
> 优先级随语言能力（lambda / async / `Arc<Mutex>` 改造等）就绪而调整。

---

## 现状回顾（2026-04-30）

已实现包：`z42.cli` / `z42.core` / `z42.collections` / `z42.diagnostics` / `z42.encoding` / `z42.io` / `z42.io.binary` / `z42.json` / `z42.math` / `z42.random` / `z42.regex` / `z42.test` / `z42.text` / `z42.time` / `z42.toml` / `z42.uri`。

覆盖能力：基础类型、协议接口、基础集合、Math、StringBuilder、Console + File / Directory / Path / Environment / Process、测试框架、编码（Hex/Base64/UTF-8）、UTC 时刻 + 时间段 + 单调计时器、TOML 1.0 子集 reader/writer、JSON RFC 8259 reader/writer。

**主要空缺**：并发、网络、加密、随机数。

---

## 分级原则

| 级别 | 含义 | 准入条件 |
|------|------|---------|
| **P0** | 必备 — 缺则无法用于任何实际项目 | 几乎所有应用都需要；不依赖未实现语言特性 |
| **P1** | 高价值 — 中型项目几乎一定会用 | 阻塞主流场景（并发 / 网络 / 加密）；可能依赖语言能力 |
| **P2** | 中等优先 — 特定场景必需 | 协议解析、压缩、配置、日志；按需排 |
| **P3** | 低优 — 生态成熟后再补 | 反射、国际化、SIMD、Pipelines |

每个包准入还需符合 [stdlib-organization.md](organization.md) §"开新包必备" 三条：
明确层级、依赖闭包、是否可纯脚本化。

---

## P0 — 必备

| 包 | C# 对标 | Rust 对标 | 层级 | extern | 备注 |
|----|---------|----------|------|--------|------|
| **z42.io.fs**（扩展现 `z42.io`）| `System.IO.File` / `Directory` / `Path` | `std::fs` / `std::path` | L2 | 走 Platform HAL | 当前 `z42.io` 仅 stdin/stdout/基础 File；缺 Directory 枚举、Path 完整 API、文件元数据 |
| **z42.os** / **z42.env** | `System.Environment` / `System.Diagnostics.Process` | `std::env` / `std::process` | L2 | 走 Platform HAL | 环境变量、命令行参数、退出码、子进程 spawn |

**起步排期**：L2 内可立刻起步（不依赖未实现语言特性），按 `z42.io.fs 扩展 → z42.os` 顺序。

**已落地（不在 P0 表）**：
- `z42.encoding`（2026-05-13 add-z42-encoding）— Hex / Base64 RFC 4648 §4 / UTF-8。详 [encoding.md](encoding.md)
- `z42.time`（2026-05-14 add-z42-time）— UTC 时刻（DateTime）/ 时间段（TimeSpan）/ 单调计时器（Stopwatch）。详 [time.md](time.md)
- `z42.toml`（2026-05-14 add-z42-toml）— TOML 1.0 subset reader/writer，覆盖 manifest 解析。详 [toml.md](toml.md)
- `z42.json`（2026-05-15 add-z42-json）— JSON RFC 8259 完整 reader/writer。详 [json.md](json.md)
- `z42.random`（2026-05-15 add-z42-random）— PCG-XSH-RR deterministic PRNG。详 [random.md](random.md)
- `z42.uri`（2026-05-15 add-z42-uri）— RFC 3986 子集 URI parser + percent codec。详 [uri.md](uri.md)
- `z42.io.binary`（2026-05-15 add-z42-io-binary）— `BinaryReader/Writer` over byte[]，LE+BE int16/32/64 + UTF-8 string。详 [io-binary.md](io-binary.md)
- `z42.diagnostics`（2026-05-15 add-z42-diagnostics）— `Log` static facade + 5 level (TRACE/DEBUG/INFO/WARN/ERROR)，stderr 输出。详 [diagnostics.md](diagnostics.md)
- `z42.regex`（2026-05-16 add-z42-regex）— RFC 子集 regex parser + backtracking 匹配引擎；Compile/IsMatch/Find/FindAll/Replace/Split。详 [regex.md](regex.md)
- `z42.cli`（2026-05-16 add-z42-cli）— ArgParser + ParseResult + auto -h/--help；Phase 0 of shell-script → z42 self-hosting。详 [cli.md](cli.md)
- `z42.threading`（2026-05-20 add-threading-stdlib）— `Std.Threading.Thread` / `Std.ThreadException`：OS 线程级 `Start(Action)` / `Join()`，跨线程 exception 经 `ThreadException.Message` 透传。底层 `__thread_spawn` / `__thread_join` builtin 接 `VmCore.threads` slot table。
- `z42.threading` 扩展（2026-05-20 add-sync-primitives）— `Std.Threading.Mutex<T>` / `Channel<T>` + `Std.ChannelDisconnectedException`：RAII callback Mutex（`Lock(Func<T,T>)`）+ unbounded MPSC channel（`Send` / `Recv` / `TryRecv` / `Close`）。底层 `__mutex_*` × 4 + `__channel_*` × 5 builtin 接 `VmCore.mutexes` / `VmCore.channels` slot table；附带修复 `GcRef::borrow` 跨线程 panic（`try_lock` → blocking `lock`）。
- `z42.compression`（2026-05-24 add-z42-compression）— `Std.Compression.{Gzip, Zlib, Deflate, Zstd}` + `Std.Archive.{Tar, Zip}` + streaming `CompressionStream`。**首个 stdlib native 走出 z42vm**：独立 cdylib `libz42_compression.{so,dylib,dll}` 经 `native::ext` loader（搜索路径 `Z42_NATIVE_PATH` + `<exe>/../native/`）按需 dlopen 加载；wasm / 移动平台经 `bundled-compression` Cargo feature 静态 link 兜底。建立了所有未来重 native stdlib（z42.net / z42.numerics）共用的"out-of-VM" 模板。详 [compression.md](compression.md) + [native-ext-loader.md](../runtime/native-ext-loader.md)。
- `z42.yaml`（2026-05-24 add-z42-yaml）— YAML 1.2 subset reader/writer。纯脚本，镜像 z42.toml / z42.json 的 `XxxValue` discriminated-union 形态。覆盖 block mapping / sequence、flow `{}` `[]`、plain / single / double 引号、`# ...` 注释；不含 anchors / tags / multi-line `|` `>` / multi-doc / 复杂键（详 [yaml.md](yaml.md) Deferred 段）。
- `Std.IO.Stream` + `MemoryStream`（2026-05-24 add-z42-io-stream）— 统一流式 I/O base class（capability `CanRead`/`CanWrite`/`CanSeek` + core `Read(buf,off,n)` / `Write(buf,off,n)` + 可选 `Length`/`Position`/`Seek` + convenience `ReadAllBytes`/`WriteAllBytes`/`ReadExactly`）+ `byte[]`-backed `MemoryStream`。z42 无 abstract 关键字 — 用 concrete base + throw stubs 模式。为后续 `FileStream` / `NetworkStream` / `TextReader` / `BufferedStream` + `CompressionStream` / `BinaryReader` refactor 铺路。详 [io-stream.md](io-stream.md)。
- `z42.net` K1（2026-05-24 add-z42-net）— `Std.Net.Sockets.{TcpClient, TcpListener, NetworkStream}` 同步阻塞 TCP。VM-side `corelib/network.rs` 内嵌 `std::net::*`，不走 cdylib；slot table 镜像 `ProcessHandle` pattern。wasm32 throw `NetUnsupportedException`。UDP / IPAddress / DNS / Timeout / TLS / HTTP 走 follow-up spec 叠加。详 [net.md](net.md)。

---

## P1 — 高价值

| 包 | C# 对标 | Rust 对标 | 层级 | extern | 阻塞依赖 |
|----|---------|----------|------|--------|---------|
| ~~z42.threading~~ | ~~`System.Threading.Thread`~~ | ~~`std::thread`~~ | ~~L2~~ | — | **✅ 已落地 2026-05-20**（add-threading-stdlib + add-sync-primitives 双 spec） |
| **z42.async** | `Task` + `async/await` + `CancellationToken` | `tokio` / `async-std` | L3 | 部分 native | **L3 async/await 语法**（roadmap L3）；标准库需先有 z42.threading 同步原语 |
| ~~z42.net K1~~ | ~~`System.Net.Sockets`~~ | ~~`std::net::Tcp*`~~ | ~~L2~~ | — | **✅ K1 已落地 2026-05-24**（add-z42-net）—— TCP-only。UDP / IPAddress / DNS / Timeout / TLS / HTTP / async 走 follow-up specs |
| **z42.crypto** | `System.Security.Cryptography` | `ring` / `sha2` / `aes` | L2 | FFI | ✅ SHA-256 + HMAC-SHA256 已落地 (2026-05-24)；KDF / 对称加密 / CSPRNG 留 follow-up |

**起步排期**：
- L2 末 / L3 初：`z42.regex` ✅ → `z42.threading` ✅ → `z42.crypto` (SHA-256 ✅ + HMAC ✅) → `z42.net` K1 ✅
- L3 中（async 语法就绪后）：`z42.async`

---

## P2 — 中等优先

| 包 | C# 对标 | Rust 对标 | 层级 | 备注 |
|----|---------|----------|------|------|
| ~~z42.compression~~ | ~~`System.IO.Compression`~~ | ~~`flate2` / `zstd`~~ | ~~L2~~ | **✅ 已落地 2026-05-24** add-z42-compression — Gzip / Zlib / Deflate / Zstd + Tar / Zip Read。首个 stdlib native 走出 z42vm（cdylib + dlopen via `native::ext` loader），详 [compression.md](compression.md) + [native-ext-loader.md](../runtime/native-ext-loader.md) |
| ~~z42.yaml~~ | ~~(社区)~~ | ~~`serde_yaml`~~ | ~~L1~~ | **✅ 已落地 2026-05-24** add-z42-yaml — YAML 1.2 subset reader/writer，纯脚本镜像 z42.toml / z42.json |
| **z42.linq** | `System.Linq` | `Iterator` trait 链式方法 | L1 | **依赖 lambda + iterator trait**（L3） |

---

## P3 — 低优

| 包 | C# 对标 | Rust 对标 | 阻塞依赖 |
|----|---------|----------|---------|
| **z42.xml** | `System.Xml` | `quick-xml` | — |
| **z42.globalization** | `System.Globalization` | `icu` crate | — |
| **z42.reflection** | `System.Reflection` | (无原生) | **L3-R 反射轨道** |
| **z42.numerics** | `System.Numerics`（BigInt / Vector）| `num` / `glam` | — |
| **z42.io.pipelines** | `System.IO.Pipelines` | `bytes` / `tokio` | 高吞吐 IO；依赖 z42.async |

---

## 决策记录

### 为什么不直接抄 C# BCL 全集

- **Script-First 约束**：`docs/design/philosophy.md` §8 要求纯脚本优先，
  能用 z42 写就不进 VM。这与 .NET BCL 大量依赖运行时 native 实现的思路不同
- **包边界对齐 Rust**：用细粒度独立 zpkg（z42.json / z42.regex 单包），
  而非 C# 的"一个 BCL 大盒子"，符合 [stdlib-organization.md](organization.md) §"Extension over Expansion"
- **不在 z42.core 扩展**：core 已 lock-in（见 stdlib-organization.md 规则 #6），
  P0/P1 大多数包都是新独立 zpkg，**不回溯改 core**

### 为什么 FFI 包（crypto / compression / regex）排在中等优先

- Tier 1/2 C ABI 已就绪（C8–C11，2026-04-29 完成）
- 包系统库（libsodium / zlib / PCRE2）比纯脚本实现快 10×+，且代码量少
- 但需先 P0 解决"基础"（时间、文件、编码），再用 FFI 包系统库锦上添花

### 为什么 z42.threading 原本排 P1（已于 2026-05-20 提前落地）

- 单线程脚本 + GC 已能跑大多数应用（Python / Node.js 单线程也能撑住主流场景）
- 多线程依赖 Value 类型 `Rc<RefCell>` → `Arc<Mutex>` 改造（roadmap A6），
  这是底层架构变更，不能轻量提前
- 延后 P0 等基础齐了一并设计，避免 API 反复

**实际落地路径**（2026-05-20）：A6 改造拆成三个独立 spec 顺次完成 —
`add-multithreading-foundation`（Value/GC backing → `Arc<Mutex>`）→
`add-vmcontext-registry`（GC 扫描跨 VmContext）→
`add-threading-stdlib`（`Std.Threading.Thread`），让 P1 第一品提前到位。
P1 剩余条目（`z42.threading.sync` / `z42.crypto` / `z42.net` / `z42.async`）
照原计划推进。

---

## 与 roadmap.md 的关系

`docs/roadmap.md` "标准库（基础）"段保持精简（只列已完成包），具体扩展计划全部
留在本文件。每完成一个新包后：

1. 从本文件对应级别移除该行
2. 添加到 [stdlib-organization.md](organization.md) "现状" 表
3. 更新 [stdlib.md](overview.md) "Module Catalog" 段（如有新对外 API）
4. `docs/roadmap.md` 标准库段添加一行简述

---

## 不在本文件范围

以下不属于"包级扩展"，不在本路线图：

- VM 内部 intrinsic 调整（属 stdlib.md L1 层）
- 现有包的 bug fix / 小特性补丁（直接走 docs/spec/changes/）
- 语言特性扩展（lambda / async / 反射）— 见 [language-overview.md](../language/language-overview.md) + roadmap L3 段

---

## Deferred / Future Work

### z42.time 延后项索引（详见 [time.md](time.md) Deferred 段）

| ID | 标题 | 前置依赖 |
|----|------|---------|
| `time-future-calendar` | 日历分解（年/月/日）+ 时区 | 时区数据库嵌入 |
| `time-future-format-parse` | ISO 8601 格式化/解析 | IFormattable + string.Format 格式说明符 |
| `time-future-sleep-timer` | Sleep / Timer | async/await 或阻塞 Sleep syscall |
| `time-future-datetime-offset` | DateTimeOffset + 时区偏移 | time-future-calendar |
| `time-rename-bench-now-ns` | `__bench_now_ns` → `__time_now_mono_ns` 重命名 | add-std-process 归档 |

### z42.compression 延后项索引（详见 [compression.md](compression.md) Deferred 段）

| ID | 标题 | 前置依赖 |
|----|------|---------|
| `compression-future-zip-write` | Zip.Write — 需 byte[][] 类型系统支持或 2-pass workaround | `add-array-of-array` 或具体用例驱动 |
| `compression-future-streaming-decode` | 真正流式解压（v0 是 accumulate-then-decompress） | 大型压缩 payload 用例（如 tail-following gz log） |
| `compression-future-brotli` | brotli 算法 | `add-z42-net` HTTP client 落地 |
| `compression-future-xz-lz4` | xz / LZ4 算法 | 具体用例驱动 |
| `compression-future-libdeflate-batch` | libdeflate 批量快通道 | bench 证据显示 batch 操作是瓶颈 |
| `compression-future-zstd-dictionary` | zstd preset dictionary | 小 payload 高 ratio 用例 |
| `compression-future-zip-encryption` | Zip 加密 read/write | 具体用例驱动 |
| `compression-future-wasm-zstd` | wasm 上 zstd 支持（需 WASI SDK 或纯 Rust zstd 实现） | WASI SDK 进入 z42 dev env 或 ruzstd 成熟 |

### z42.net 延后项索引（详见 [net.md](net.md) Deferred 段）

| ID | 标题 | 前置依赖 |
|----|------|---------|
| `net-future-udp` | UDP sockets (`UdpClient`) | 用例驱动；TCP 优先 |
| `net-future-ipaddress` | `IPAddress` / `IPEndPoint` 强类型 + v4/v6 parser | DNS / IPv6 显式场景 |
| `net-future-dns` | `Std.Net.Dns.GetHostAddresses(host)` | HTTP / SRV / 高级 lookup |
| `net-future-timeout` | read/write/connect timeout | 防御性超时用例 |
| `net-future-tls` | TLS / HTTPS (rustls / OpenSSL cdylib) | HTTP client / 安全 RPC |
| `net-future-http` | HTTP/1.1 client (`HttpClient.Get/Post`) | net-future-tls + 用例 |
| `net-future-async` | async/await sockets | L3 async/await 语法 |
| `net-future-socket-options` | SO_REUSEADDR / SO_KEEPALIVE / Nagle / 等 socket opts | 高性能服务调优用例 |
| `net-future-wasm-wasi-sockets` | wasm32 真实 socket via WASI-sockets | WASI-sockets 标准 stable + runtime 支持 |

### z42.yaml 延后项索引（详见 [yaml.md](yaml.md) Deferred 段）

| ID | 标题 | 前置依赖 |
|----|------|---------|
| `yaml-future-anchors` | `&id` / `*id` anchor 与 alias | Helm/K8s manifest 反复引用用例 |
| `yaml-future-tags` | `!str` / `!!binary` / 用户自定义 `!tag` | tag 注册表 + 用例 |
| `yaml-future-multiline-strings` | `\|` literal / `>` folded 多行 scalar | Helm templates / embedded scripts 用例 |
| `yaml-future-multi-doc` | 多文档（`---` 分隔多个 doc） | `kubectl`-style stacked manifest |
| `yaml-future-complex-keys` | sequence / mapping 作为 key（`? key` 语法） | 用例驱动 |
| `yaml-future-timestamps` | ISO-8601 timestamp scalar | z42.time DateTime 稳定 |
| `yaml-future-numeric-bases` | octal `0o7` / hex `0xFF` int literal | 用例驱动 |

### Std.IO.Stream 延后项索引（详见 [io-stream.md](io-stream.md) Deferred 段）

| ID | 标题 | 前置依赖 |
|----|------|---------|
| ~~`io-stream-future-filestream`~~ | **✅ 已落地 2026-05-24** — `Std.IO.FileStream : Stream` + `FileMode.Read/Write/Append`，8 个 corelib builtin (`__file_*`) 走 `VmCore.file_handles` slot table | — |
| `io-stream-future-textreader` | `TextReader / TextWriter`（编码 + 行处理） | 行式 IO 用例 |
| ~~`io-stream-future-bufferedstream`~~ | **✅ 已落地 2026-05-24**（add-buffered-stream）— single-buffer Read/Write batching wrapper，4 KB default，大 IO bypass | — |
| `io-stream-future-async` | `ReadAsync` / `WriteAsync` | L3 async/await |
| ~~`io-stream-future-copy-to`~~ | **✅ 已落地 2026-05-24**（add-stream-copy-to）— `Stream.CopyTo(Stream)` + `CopyTo(Stream, int)`，4 KB default buffer | — |
| ~~`refactor-compression-stream-on-iostream`~~ | **✅ 已落地 2026-05-24** — CompressionStream 删除，改 generic `_CompressionEncoderStream / _CompressionDecoderStream extends Stream` + `Gzip/Zlib/Deflate/Zstd.WrapWrite(Stream) / WrapRead(Stream)` | — |
| ~~`refactor-binary-reader-stream`~~ | **✅ 已落地 2026-05-24** — BinaryReader/Writer 后端 byte[] → Stream，新增 `(Stream)` 构造器；byte[] 构造保留作 sugar | — |
| ~~`process-stream-stdio`~~ | **✅ 已落地 2026-05-24** — `ProcessStdinStream` + `ProcessOutputStream(fd)`，`ProcessHandle.GetStdinStream/GetStdoutStream/GetStderrStream`；2 个 `__process_handle_read_*` builtin | — |
| ~~`add-z42-io-string-reader-writer`~~ | **✅ 已落地 2026-05-24** — char-oriented `StringReader` (`Peek` / `Read` / `ReadLine` / `ReadToEnd`) + `StringWriter` (`Write` / `WriteLine` / `ToString` / `Clear`)，纯脚本，无 VM 改动 | — |
| ~~`io-stream-future-streamreader-writer`~~ | **✅ 已落地 2026-05-24**（add-encoding-and-stream-text）— `Std.Encoding.Encoding` + `Std.IO.StreamReader(Stream[, Encoding])` + `StreamWriter(Stream[, Encoding])`，v0 drain-and-decode | — |
| `io-stream-future-streamreader-chunked` | True chunked-decode `Decoder` for unbounded / 10 GB streams | stateful `Encoding.GetDecoder()` API |
| `io-stream-future-objectdisposed` | `ObjectDisposedException` + `_closed` 追踪 | z42 IDisposable / using 语法 |
| `io-stream-future-end-of-stream-exception` | 专用 `EndOfStreamException`（v0 复用 InvalidOperationException） | `add-z42-io-exceptions` 独立 spec |

### z42 build-driver prerequisites（2026-05-13）

**触发来源**：仓库 build / test 脚本目前全是 bash（`scripts/*.sh` + `src/toolchain/host/platforms/*/build.sh`），不能在 Windows 上跑。要让 Tier 1 Windows CI 工作，有 4 条路（xtask / Python driver / PowerShell 双维护 / Git Bash）—— 2026-05-13 决策**全部放弃**，长期目标是**用 z42 自身重写所有 build / test 脚本**：编译为 `.zbc` 单文件，由 `z42vm` 跨平台执行，与 z42 自举路线一致（参 [`roadmap.md`](../../roadmap.md) L4 自举段）。

**问题**：直接做不可行，z42 stdlib 缺一组 build driver 必需的原语。即便先用 native interop（`extern "C"`）shell out 到 libc，也只解决 POSIX，不解决 Windows（Win32 API 还得另封一层）—— 必须等下面这些**跨平台抽象**层在 stdlib 中先到位。

**阻塞清单**（每项对照本路线图分级；✅ = 已落地）：

| 原语 | 本路线图分级 | 用途 | 状态 |
|------|------------|------|------|
| `Std.IO.Process.{Run, Spawn}` 带 stdout capture / exit code / timeout / stdio 四态 | **P0 z42.io.process** | spawn `cargo build` / `dotnet test` / `gradle` / `xcodebuild` / `wasm-pack` | ✅ 2026-05-14 add-std-process（在 z42.io 而非独立 z42.os 包；与 File / Directory / Path / Environment 一起共享 host FFI 边界）|
| `Std.IO.File.{ReadAllText, WriteAllText, Exists, Copy, Move, Delete}` | **P0 z42.io.fs** | 读 versions.toml / 写 manifest.toml / 拷 zpkg | ✅ 2026-05-12 add-std-io-polish |
| `Std.IO.Directory.{Create, Enumerate, Exists}` | **P0 z42.io.fs** | mkdir -p / 扫 src/tests/<cat>/<name>/ | ✅ 2026-05-13 add-std-io-directory |
| `Std.IO.Path.{Combine, GetDirectoryName, GetExtension, IsRooted, ...}` | **P0 z42.io.fs** | 跨平台 path 拼接（避免 `/` vs `\` 错误） | ✅ 2026-05-12 add-std-io-polish |
| `Std.Environment.{GetVar, SetVar}` + `Std.Environment.GetCommandLineArgs()` | **P0 z42.os** + 已有 | 读 `$ANDROID_NDK_HOME` / argv 解析 | ✅ 2026-05-12 add-std-io-polish |
| `Std.Toml.TomlValue.Parse(text)` / `Stringify(root)` | **P2 z42.toml** | 解析 versions.toml；shell 端目前用 python3 + tomllib | ✅ 2026-05-14 add-z42-toml |
| `Std.Net.Http.Get(url) -> stream` 或 shell out 到 curl | **P1 z42.net**（先 curl 顶着）| NDK / SDK 压缩包下载 | — |
| `Std.Crypto.SHA256` 或 shell out 到 shasum/openssl | **P1 z42.crypto**（先 shasum 顶着）| 下载校验 | — |
| `Std.IO.Compression.Zip.Extract` 或 shell out 到 unzip | **P2 z42.compression**（先 unzip 顶着）| NDK zip 解压 | — |

**触发条件 / 可恢复推进点**：

1. **P0 三件套先到位**（z42.os Process / z42.os Environment / z42.io.fs File+Directory+Path）—— 估计与 L2 M7-M8 标准库主体推进同节奏。这三项落地后，**大多数 bash 脚本**（`test-all.sh` / `test-vm.sh` / `test-cross-zpkg.sh` / `test-stdlib.sh` / `regen-golden-tests.sh`）已经可以 1:1 迁成 `.z42`（shell out 到 curl / unzip / shasum 暂时顶住下载类）
2. **P1 z42.crypto + P1 z42.net 中后期** —— `setup-tools.sh` 的 NDK 下载 + sha256 校验从 shell-out 升级为纯 z42 端 API
3. **P2 z42.toml + z42.compression** —— `versions.toml` 解析改 z42 原生；NDK zip 解压改 z42 原生
4. **打通后**：删除 `scripts/_lib/versions.sh`（python3 依赖也跟着下）+ 所有 `.sh` → `.z42`，CI 第一步 `cargo build z42vm; ./z42vm scripts/test-all.zbc`，Tier 1 Windows 通

**当前 workaround**：

- 仓内继续 bash 驱动（macOS / Linux dev workflow 不受影响）
- Windows dev：用 WSL / Git Bash（接受 path 翻译等 quirks）；Tier 1 Windows CI 推迟到 z42 build-driver 落地
- 不投资中间态：**不做 xtask（Rust）**、**不做 PowerShell 双维护**、**不做 Python driver** —— 中间态的代码在 z42 driver 上线后必删，做了就是技术债

**与 stdlib P0/P1/P2 顺序的关系**：这条 backlog **不**强行抢占现有 P0 排期（`z42.io.fs 扩展 → z42.os → z42.json`）。z42.os / z42.io.fs 推进时附带验证它们能驱动 build script 即可。
