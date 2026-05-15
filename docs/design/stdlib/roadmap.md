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

已实现包：`z42.core` / `z42.collections` / `z42.diagnostics` / `z42.encoding` / `z42.io` / `z42.io.binary` / `z42.json` / `z42.math` / `z42.random` / `z42.regex` / `z42.test` / `z42.text` / `z42.time` / `z42.toml` / `z42.uri`。

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

---

## P1 — 高价值

| 包 | C# 对标 | Rust 对标 | 层级 | extern | 阻塞依赖 |
|----|---------|----------|------|--------|---------|
| **z42.threading** | `System.Threading`（`Thread` / `Mutex` / `Monitor` / `Channel`）| `std::thread` / `std::sync` / `crossbeam` | L2 | 通过 Platform HAL | **roadmap A6: Value `Rc<RefCell>` → `Arc<Mutex>`** + GC `Send` 设计；与并发模型一并 |
| **z42.async** | `Task` + `async/await` + `CancellationToken` | `tokio` / `async-std` | L3 | 部分 native | **L3 async/await 语法**（roadmap L3）；标准库需先有 z42.threading 同步原语 |
| **z42.net** | `System.Net.Sockets` / `System.Net.Http` | `std::net` + `hyper` / `reqwest` | L2 | 走 Tier1 C ABI（系统 socket 或 libcurl） | 先 TCP/UDP（同步）；HTTP client 留次级 |
| **z42.crypto** | `System.Security.Cryptography` | `ring` / `sha2` / `aes` | L2 | FFI（libsodium / OpenSSL 最快）| 哈希（SHA-2/3）+ CSPRNG + 对称加密 |

**起步排期**：
- L2 末 / L3 初（A6 改造完成后）：`z42.regex` → `z42.threading` → `z42.crypto` → `z42.net`
- L3 中（async 语法就绪后）：`z42.async`

---

## P2 — 中等优先

| 包 | C# 对标 | Rust 对标 | 层级 | 备注 |
|----|---------|----------|------|------|
| **z42.compression** | `System.IO.Compression` | `flate2` / `zstd` | L2 | gzip / zstd；FFI 包 zlib 优先 |
| **z42.toml** / **z42.yaml** | (社区) | `toml` / `serde_yaml` | L1 | 工程文件已用 toml，可先内部用，再公开 |
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
