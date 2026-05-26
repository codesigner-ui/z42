# stdlib P1 / P2 迭代 backlog

> 创建 2026-05-27。本轮 stdlib gap 盘点剩余项，逐个落地后划掉。**已完成项不删行**，标 ✅ 留痕；全部完成后整文归档到 `docs/spec/archive/`。

## P1 — 高频但仍缺

| 项 | 估时 | 状态 | Commit | 简述 |
|----|:----:|:----:|:------:|------|
| `Crypto.Pbkdf2(password, salt, iterations, keyLen)` | 1h | ✅ | `eee5941d` | 纯 z42 atop 既有 `Hmac.Sha256`；密码哈希必备。RFC 7914 §11 vector byte-match |
| `Json.SelectPath("$.user.name")` 嵌套访问 | 2h | ✅ | `726309de` | `JsonPath.Select(root, path)` — 支持 `.name` / `[N]` / `["key"]`；missing/oor/wrong-kind 返 null |
| `Zip.ExtractAllTo(bytes, dir)` | 2h | ✅ | `a9c5f2a7` | 纯 z42 atop `Zip.Read`；Zip-Slip 防御 + 目录条目处理；附带修了 Zip `new string(chars)` 同 B2 latent bug。`CreateFromDirectory` 等 `Zip.Write` deferred 解锁后再做 |
| `Collections.PriorityQueue<T>` 二叉堆 | 1.5h | ✅ | `e7289743` | 纯 z42 + `where T : IComparable<T>`，最小堆（O(log n) Enqueue/Dequeue） |
| `Diagnostics` 结构化日志（`Log.Info(msg, fields)`） | 2h | ✅ | `1ea35472` | `LogFields` builder + 5 个 level `(msg, fields)` 重载；logfmt 格式带 `\` `"` escape |

## P2 — nice-to-have

| 项 | 估时 | 状态 | Commit | 简述 |
|----|:----:|:----:|:------:|------|
| `Time.TimeZone` 基础 + 短代码 → UTC offset 表 | 3h+ | ✅ | `9c5bb11c` | 固定 offset + ~22 短代码（UTC/GMT/EST/PST/JST/IST/...）；`DateTime.ToIso8601With(tz)` 配套渲染。无 DST、无 IANA 完整库（仍 Deferred） |
| `Cli.Subcommand`（ArgParser 树形） | 2h | ✅ | _pending_ | `SubcommandRouter.Add(name, desc, ArgParser) + Match(argv)` 派发；`SubcommandMatch` 包装结果。不修 ArgParser 自身 |
| `Text.Levenshtein(a, b)` / `SimilarityRatio` | 1h | ⏳ | — | 纯 z42；fuzzy search / 自动补全 |
| `Encoding.UTF16/UTF32` | 3h | ⏳ | — | 纯 z42；Windows PE 字符串 + .NET native interop |
| 并行 `[Test]` 执行 | 大 | ⏳ | — | test-runner 重构；当前串行 fork 子进程 |
| `Std.IO.Stream.Seek` 边界验证 | 30m | ⏳ | — | 当前可超界，应抛 `IOException` |
| `ProcessHandle.WriteStdin(byte[])` 重载 | 30m | ⏳ | — | 当前只接 string；与 stdout 字节流不对称 |

## 实施记录

按表顺序逐项做。每项完成时：
1. 改本表状态 → ✅ + 填 commit hash
2. 视情况把该项的 Deferred 段从对应 design doc 移除（如有）

落地的 commit / spec dir 通过 git log 查到，本表不冗余 cross-link。

## 已知接合点（实施期可能遇到的 backlog）

- `compiler-future-typed-overload-resolution`：`override` 父方法的 arity overload 不支持（add-bigint-tobase 撞到）；可能影响其他 stdlib 类的 API 命名选择
- `compression-future-zip-write`：`Zip.Write` 仍未实现（compression.md Deferred）；本表 `Zip.CreateFromDirectory` 依赖该项，若 Zip.Write 没就绪需要单独立 spec 把 v0 Zip.Write 落地，再做 CreateFromDirectory
- test-runner Arc<str> 类型错（其他 in-flight session 在修）：所有 [Test] 端到端 GREEN 都等它落地
