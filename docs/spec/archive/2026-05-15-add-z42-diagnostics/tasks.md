# Tasks: add z42.diagnostics

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

stdlib roadmap P2 表里的 `z42.diagnostics`：日志门面 + 简单 sink。替代当前
ad-hoc `Console.WriteLine` 调试。对标 C# `System.Diagnostics.Trace` /
Rust `log` crate（facade only，sink 可插拔）。

v0 最小：单 global `Log` static class + 5 个 level + ConsoleError sink
(stderr 输出避免污染 stdout) + min-level filter。命名 logger / 多 sink /
JSON 结构化输出留 follow-up。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. API 形态 | (a) named loggers `Log.Get("module")` / (b) global `Log` static | (b) | v0 简单；named/scoped 留 follow-up |
| 2. Sink | (a) hardcoded stderr / (b) pluggable list | (a) | v0 单一；Pluggable sink list 留 follow-up（需 delegate / interface） |
| 3. Level 数 | (a) 5 (Trace/Debug/Info/Warn/Error) / (b) 3 / (c) syslog 8 | (a) | 工业惯例；C# / Rust / Java 通用 |
| 4. 输出目标 | (a) stdout / (b) stderr | (b) | Console.WriteLine 已是 stdout 主用途；日志走 stderr 不污染程序输出 |
| 5. Level 表达 | (a) enum / (b) int constants 类 | (b) | z42 暂无 enum；同 z42.json `JsonKind` 模式（int + static const 类） |
| 6. 时间戳 | (a) UnixMs / (b) ISO8601 / (c) 无 | (a) | DateTime.ToString 目前返回 UnixMs；ISO8601 等 z42.time 落地 strftime |
| 7. Format | (a) `[LEVEL ms] msg` / (b) JSON | (a) | 人类可读优先；JSON 结构化留 follow-up |
| 8. 默认 min level | (a) Info / (b) Debug / (c) Warn | (a) | 工业默认；Debug 噪声大 |
| 9. Namespace | `Std.Diagnostics`（top-level） | yes | 与所有现有 stdlib 一致；避免 nested-ns bug |

## 阶段 1: 包骨架

- [x] 1.1 NEW `src/libraries/z42.diagnostics/z42.diagnostics.z42.toml` — manifest（dep on z42.core + z42.io + z42.time）
- [x] 1.2 NEW `src/libraries/z42.diagnostics/src/LogLevel.z42`
  - `namespace Std.Diagnostics;`
  - `public static class LogLevel { TRACE=0 DEBUG=1 INFO=2 WARN=3 ERROR=4 + Name(int) }`
- [x] 1.3 NEW `src/libraries/z42.diagnostics/src/Log.z42`
  - `namespace Std.Diagnostics;`
  - `public static class Log` with `_minLevel`
  - `SetMinLevel(int) / GetMinLevel() / IsEnabled(int)`
  - `Trace(string) / Debug(string) / Info(string) / Warn(string) / Error(string)`
  - 内部 `_write(level, msg)` → format `[LEVEL ms] msg` → `ConsoleError.WriteLine`

## 阶段 2: 测试

- [x] 2.1 NEW `tests/log_basic.z42` — 5 level method exist + IsEnabled gates
- [x] 2.2 NEW `tests/log_filter.z42` — SetMinLevel filters

## 阶段 3: Wiring + docs

- [x] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.diagnostics"`
- [x] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.Diagnostics`
- [x] 3.3 NEW `src/libraries/z42.diagnostics/README.md`
- [x] 3.4 NEW `docs/design/stdlib/diagnostics.md`
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/build-stdlib.sh` 全绿
- [x] 4.2 `./scripts/test-stdlib.sh z42.diagnostics` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.4 mv → `docs/spec/archive/2026-05-15-add-z42-diagnostics/`
- [x] 4.5 commit + push
