# z42 标准库扩展路线图

> **目的**：列出 z42 当前缺失但中长期必备的标准库包，按重要程度分级，
> 参考 C# BCL 与 Rust std/生态的做法，给后续 spec proposal 提供优先级依据。
>
> **受众**：stdlib 设计者、新包提案者、roadmap reviewer。
>
> **配套文档**：
> - 架构契约：[stdlib.md](stdlib.md)（三层模型 + `[Native]` 规则）
> - 划分规则：[stdlib-organization.md](stdlib-organization.md)（包边界 + 层级硬约束）
> - 当前实现状态：[../../src/libraries/README.md](../../src/libraries/README.md)（Extern 现状审计）
>
> **更新策略**：每完成一个新包后，把对应行从本文件移除（迁移到 stdlib-organization.md 现状表）；
> 优先级随语言能力（lambda / async / `Arc<Mutex>` 改造等）就绪而调整。

---

## 现状回顾（2026-04-30）

已实现包：`z42.core` / `z42.collections` / `z42.io` / `z42.math` / `z42.test` / `z42.text`。

覆盖能力：基础类型、协议接口、基础集合、Math、StringBuilder、Console + File 基础、测试框架。

**主要空缺**：时间、文件系统的目录/路径深度操作、环境/进程、编码与序列化、并发、网络、加密、随机数。

---

## 分级原则

| 级别 | 含义 | 准入条件 |
|------|------|---------|
| **P0** | 必备 — 缺则无法用于任何实际项目 | 几乎所有应用都需要；不依赖未实现语言特性 |
| **P1** | 高价值 — 中型项目几乎一定会用 | 阻塞主流场景（并发 / 网络 / 加密）；可能依赖语言能力 |
| **P2** | 中等优先 — 特定场景必需 | 协议解析、压缩、配置、日志；按需排 |
| **P3** | 低优 — 生态成熟后再补 | 反射、国际化、SIMD、Pipelines |

每个包准入还需符合 [stdlib-organization.md](stdlib-organization.md) §"开新包必备" 三条：
明确层级、依赖闭包、是否可纯脚本化。

---

## P0 — 必备

| 包 | C# 对标 | Rust 对标 | 层级 | extern | 备注 |
|----|---------|----------|------|--------|------|
| **z42.time** | `System.DateTime` / `Stopwatch` / `TimeSpan` | `std::time` / `chrono` | L1 | 通过 z42.core 的 `__time_now_ms` | 几乎所有日志、超时、调度都需要；UTC + 单调时钟 |
| **z42.io.fs**（扩展现 `z42.io`）| `System.IO.File` / `Directory` / `Path` | `std::fs` / `std::path` | L2 | 走 Platform HAL | 当前 `z42.io` 仅 stdin/stdout/基础 File；缺 Directory 枚举、Path 完整 API、文件元数据 |
| **z42.os** / **z42.env** | `System.Environment` / `System.Diagnostics.Process` | `std::env` / `std::process` | L2 | 走 Platform HAL | 环境变量、命令行参数、退出码、子进程 spawn |
| **z42.encoding** | `System.Text.Encoding` / `System.Convert` | `std::str` + `base64` / `hex` | L1 | 纯脚本（基于 byte buffer） | UTF-8/16、Base64、Hex；FFI、网络协议刚需 |
| **z42.json** | `System.Text.Json` | `serde_json` | L1 | 纯脚本 | reader/writer 优先；serde-like derive 留 L3（依赖反射） |

**起步排期**：L2 内可立刻起步（不依赖未实现语言特性），按 `z42.time → z42.io.fs 扩展 → z42.os → z42.encoding → z42.json` 顺序。

---

## P1 — 高价值

| 包 | C# 对标 | Rust 对标 | 层级 | extern | 阻塞依赖 |
|----|---------|----------|------|--------|---------|
| **z42.threading** | `System.Threading`（`Thread` / `Mutex` / `Monitor` / `Channel`）| `std::thread` / `std::sync` / `crossbeam` | L2 | 通过 Platform HAL | **roadmap A6: Value `Rc<RefCell>` → `Arc<Mutex>`** + GC `Send` 设计；与并发模型一并 |
| **z42.async** | `Task` + `async/await` + `CancellationToken` | `tokio` / `async-std` | L3 | 部分 native | **L3 async/await 语法**（roadmap L3）；标准库需先有 z42.threading 同步原语 |
| **z42.net** | `System.Net.Sockets` / `System.Net.Http` | `std::net` + `hyper` / `reqwest` | L2 | 走 Tier1 C ABI（系统 socket 或 libcurl） | 先 TCP/UDP（同步）；HTTP client 留次级 |
| **z42.regex** | `System.Text.RegularExpressions` | `regex` crate | L1/L2 | 可 FFI（PCRE2）或纯脚本 | text 处理常用；`Std.Text.Regex` 已占位 |
| **z42.crypto** | `System.Security.Cryptography` | `ring` / `sha2` / `aes` | L2 | FFI（libsodium / OpenSSL 最快）| 哈希（SHA-2/3）+ CSPRNG + 对称加密 |
| **z42.random** | `System.Random` | `rand` crate | L1 | 通用伪随机纯脚本；安全随机走 Platform HAL | 可拆出独立轻量包，先于 crypto |

**起步排期**：
- L2 末 / L3 初（A6 改造完成后）：`z42.random` → `z42.regex` → `z42.threading` → `z42.crypto` → `z42.net`
- L3 中（async 语法就绪后）：`z42.async`

---

## P2 — 中等优先

| 包 | C# 对标 | Rust 对标 | 层级 | 备注 |
|----|---------|----------|------|------|
| **z42.io.binary**（z42.io 子模块）| `BinaryReader/Writer` | `byteorder` | L2 | 二进制流读写；协议解析、自定义文件格式 |
| **z42.compression** | `System.IO.Compression` | `flate2` / `zstd` | L2 | gzip / zstd；FFI 包 zlib 优先 |
| **z42.toml** / **z42.yaml** | (社区) | `toml` / `serde_yaml` | L1 | 工程文件已用 toml，可先内部用，再公开 |
| **z42.linq** | `System.Linq` | `Iterator` trait 链式方法 | L1 | **依赖 lambda + iterator trait**（L3） |
| **z42.diagnostics** | `System.Diagnostics.Trace` / `EventSource` | `log` / `tracing` | L1 | 结构化日志；先做 facade，sink 可插拔 |
| **z42.uri** | `System.Uri` | `url` crate | L1 | URL 解析；net 之前可独立 |

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
  而非 C# 的"一个 BCL 大盒子"，符合 [stdlib-organization.md](stdlib-organization.md) §"Extension over Expansion"
- **不在 z42.core 扩展**：core 已 lock-in（见 stdlib-organization.md 规则 #6），
  P0/P1 大多数包都是新独立 zpkg，**不回溯改 core**

### 为什么 FFI 包（crypto / compression / regex）排在中等优先

- Tier 1/2 C ABI 已就绪（C8–C11，2026-04-29 完成）
- 包系统库（libsodium / zlib / PCRE2）比纯脚本实现快 10×+，且代码量少
- 但需先 P0 解决"基础"（时间、文件、编码），再用 FFI 包系统库锦上添花

### 为什么 z42.threading 排 P1 而非 P0

- 单线程脚本 + GC 已能跑大多数应用（Python / Node.js 单线程也能撑住主流场景）
- 多线程依赖 Value 类型 `Rc<RefCell>` → `Arc<Mutex>` 改造（roadmap A6），
  这是底层架构变更，不能轻量提前
- 延后 P0 等基础齐了一并设计，避免 API 反复

---

## 与 roadmap.md 的关系

`docs/roadmap.md` "标准库（基础）"段保持精简（只列已完成包），具体扩展计划全部
留在本文件。每完成一个新包后：

1. 从本文件对应级别移除该行
2. 添加到 [stdlib-organization.md](stdlib-organization.md) "现状" 表
3. 更新 [stdlib.md](stdlib.md) "Module Catalog" 段（如有新对外 API）
4. `docs/roadmap.md` 标准库段添加一行简述

---

## 不在本文件范围

以下不属于"包级扩展"，不在本路线图：

- VM 内部 intrinsic 调整（属 stdlib.md L1 层）
- 现有包的 bug fix / 小特性补丁（直接走 spec/changes/）
- 语言特性扩展（lambda / async / 反射）— 见 [language-overview.md](language-overview.md) + roadmap L3 段
