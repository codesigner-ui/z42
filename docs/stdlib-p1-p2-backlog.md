# stdlib P1 / P2 迭代 backlog

> 创建 2026-05-27。本轮 stdlib gap 盘点剩余项，逐个落地后划掉。**已完成项不删行**，标 ✅ 留痕；全部完成后整文归档到 `docs/spec/archive/`。

## P1 — 高频但仍缺

| 项 | 估时 | 状态 | Commit | 简述 |
|----|:----:|:----:|:------:|------|
| `Crypto.Pbkdf2(password, salt, iterations, keyLen)` | 1h | ⏳ | — | 纯 z42 atop 既有 `Hmac.Sha256`；密码哈希必备 |
| `Json.SelectPath("$.user.name")` 嵌套访问 | 2h | ⏳ | — | 纯 z42 简化 JSONPath subset；深层数据访问 |
| `Zip.CreateFromDirectory(dir)` / `Zip.ExtractAllTo(bytes, dir)` | 2h | ⏳ | — | 纯 z42 atop `Zip.Read`；注：`Zip.Write` 仍是 deferred |
| `Collections.PriorityQueue<T>` 二叉堆 | 1.5h | ⏳ | — | 纯 z42 + `where T : IComparable<T>` |
| `Diagnostics` 结构化日志（`Log.Info(msg, fields)`） | 2h | ⏳ | — | API 设计 + impl；现 stdlib 只有 string-only |

## P2 — nice-to-have

| 项 | 估时 | 状态 | Commit | 简述 |
|----|:----:|:----:|:------:|------|
| `Time.TimeZone` 基础 + IANA → UTC offset 表 | 3h+ | ⏳ | — | 纯 z42；跨时区业务最低限度支持 |
| `Cli.Subcommand`（ArgParser 树形） | 2h | ⏳ | — | git/cargo 风格 multi-command CLI 工具 |
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
