# z42.diagnostics

## 职责
全局日志门面 + level filter，输出到 stderr。替代 ad-hoc `Console.WriteLine`
调试。对标 C# `System.Diagnostics.Trace` + Rust `log` crate（facade only）。

**不包含**：sink 路由（文件 / syslog / OTel）、结构化字段、async / batch
buffering。v0 单一 stderr 文本 sink 优先。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/LogLevel.z42` | `LogLevel` static class — TRACE/DEBUG/INFO/WARN/ERROR 常量 + `Name(int)` |
| `src/Log.z42`      | `Log` static class — `Trace/Debug/Info/Warn/Error/SetMinLevel/GetMinLevel/IsEnabled` |

## 入口点

```z42
using Std.Diagnostics;

Log.SetMinLevel(LogLevel.DEBUG);  // 默认 INFO

Log.Trace("entered method");      // < INFO，默认丢弃
Log.Debug("connecting to " + url);
Log.Info("server ready on port " + port.ToString());
Log.Warn("slow query: " + ms.ToString() + "ms");
Log.Error("disk full: " + path);

// expensive 日志：先 check
if (Log.IsEnabled(LogLevel.DEBUG)) {
    Log.Debug("dump: " + bigObject.ToString());
}
```

## 输出格式

```
[INFO 1747327200000] connecting to api.example.com
[ERROR 1747327201234] timeout after 1000ms
```

`[LEVEL UnixMs] message`，写入 stderr（`ConsoleError.WriteLine`），不污染
stdout 主输出。

## Level 顺序

```
TRACE (0) < DEBUG (1) < INFO (2, default) < WARN (3) < ERROR (4)
```

`SetMinLevel(LogLevel.WARN)` 后只输出 WARN + ERROR。

## 依赖关系
依赖 `z42.core`（基础类型）+ `z42.io`（`ConsoleError.WriteLine`）+ `z42.time`
（`DateTime.UtcNow().UnixMs()` 时间戳）。

## 不在本期 Scope（详 `docs/design/stdlib/diagnostics.md` Deferred）

- Named loggers / scoped loggers（`Log.Get("module.x")`）
- 多 sink 路由（File / network / OTel exporter）
- 结构化字段（key-value pairs / JSON output）
- Async / batch buffering
- ISO8601 timestamp（等 z42.time 落地 strftime）
- 颜色编码（terminal capability detection）
