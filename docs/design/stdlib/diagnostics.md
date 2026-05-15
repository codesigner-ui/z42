# z42.diagnostics — Structured logging facade

> 落地版本：2026-05-15（add-z42-diagnostics）
> 包路径：`src/libraries/z42.diagnostics/`
> 命名空间：`Std.Diagnostics`

## 职责

全局日志门面 + 最低级别 filter，输出到 stderr。v0 单一文本 sink，5 level，
默认 INFO。

**对标**：C# `System.Diagnostics.Trace` + Rust `log` crate（facade only，
不包含 sink 实现路由）。

## API surface

```z42
class LogLevel {                       // static, int constants + Name
    static int TRACE = 0
    static int DEBUG = 1
    static int INFO  = 2     // default min
    static int WARN  = 3
    static int ERROR = 4
    static string Name(int level)
}

class Log {                            // static facade
    static void SetMinLevel(int level)
    static int  GetMinLevel()
    static bool IsEnabled(int level)
    static void Trace(string msg)
    static void Debug(string msg)
    static void Info(string msg)
    static void Warn(string msg)
    static void Error(string msg)
}
```

## 输出格式

```
[LEVEL UnixMs] message
```

例：

```
[INFO 1747327200000] connecting to api.example.com
[ERROR 1747327201234] timeout after 1000ms
```

写入 `ConsoleError`（stderr），不污染 `Console.WriteLine` 的 stdout 主用途。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. API 形态 | named loggers / global static | global static | v0 简单；named 留 follow-up |
| 2. Sink | hardcoded stderr / pluggable list | hardcoded | v0 单一；pluggable 需 delegate / interface |
| 3. Level 数 | 5 (Trace/Debug/Info/Warn/Error) / 3 / syslog 8 | 5 | 工业惯例（C#/Rust/Java） |
| 4. 输出 | stdout / stderr | stderr | stdout 留给程序主输出 |
| 5. Level 表达 | enum / int constants | int constants | z42 暂无 enum；同 z42.json `JsonKind` |
| 6. 时间戳 | UnixMs / ISO8601 / 无 | UnixMs | DateTime 当前 ToString 即 UnixMs；ISO 留给 strftime |
| 7. Format | 文本 / JSON | 文本 | 人类可读优先 |
| 8. 默认 min | Info / Debug / Warn | Info | 工业默认；Debug 噪声大 |
| 9. Namespace | Std.Diagnostics | yes | 单段，与所有 stdlib 一致 |

## 实现结构

```
src/LogLevel.z42  (~25 行)
└── LogLevel static class — int constants + Name(int) → string

src/Log.z42  (~50 行)
└── Log static class — _minLevel + 5 level methods + _write helper
```

## 调用模式

```z42
using Std.Diagnostics;

void HandleRequest(int id) {
    Log.Info("request " + id.ToString() + " started");
    try {
        // ... work ...
        Log.Info("request " + id.ToString() + " ok");
    } catch (Exception e) {
        Log.Error("request " + id.ToString() + " failed: " + e.Message);
    }
}

// expensive log 先 check
if (Log.IsEnabled(LogLevel.DEBUG)) {
    Log.Debug("state dump: " + Dump(state));   // Dump only runs if enabled
}
```

## 不支持（Deferred）

### diagnostics-future-named-loggers

- **来源**：`Log.Get("module.x")` → 模块化 logger 树 + 各级独立 min level
- **触发原因**：v0 单一 global 即满足；树状结构需 hash map + name parser
- **触发条件**：用户场景出现"按模块过滤日志"需求
- **当前 workaround**：用 message prefix `"[module.x] ..."`

### diagnostics-future-pluggable-sinks

- **来源**：写入文件 / 网络 / Jaeger / OTel collector
- **触发原因**：需要 sink trait（z42 暂无 trait）或 delegate list；架构性
- **前置依赖**：L2/L3 trait / interface 系统 + lambda
- **当前 workaround**：调用方自行包装 Log + 业务 sink

### diagnostics-future-structured-fields

- **来源**：`Log.Info("user logged in", { "id": 42, "ip": "..." })`
- **触发原因**：v0 string-only；结构化字段需要 dict / JsonValue
- **触发条件**：用户出现需要按字段查询日志的场景
- **当前 workaround**：调用方手动 JSON.Stringify 嵌入消息

### diagnostics-future-iso8601-timestamps

- **来源**：`[INFO 2026-05-15T14:00:00.000Z]` 替代 UnixMs
- **触发原因**：人类可读；与日志聚合工具（Splunk / ELK）兼容
- **前置依赖**：z42.time 落地 `DateTime.ToIsoString()` / strftime-like API
- **当前 workaround**：UnixMs + 外部转换工具

### diagnostics-future-color

- **来源**：terminal 输出 WARN 黄、ERROR 红
- **触发原因**：需要 TTY 检测 (`isatty`) + ANSI escape；需 z42.io 扩展
- **前置依赖**：z42.io 落地 `Console.IsTerminal()` + ANSI helper
- **当前 workaround**：调用方手贴 ANSI escape

### diagnostics-future-async-batch

- **来源**：批量缓冲，减少 syscall（高吞吐场景）
- **触发原因**：v0 直接写 stderr 已够大多数场景
- **前置依赖**：z42.threading / async

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型）
- 依赖 `z42.io`（`ConsoleError.WriteLine` 写 stderr）
- 依赖 `z42.time`（`DateTime.UtcNow().UnixMs()` 时间戳）
- 被未来 z42.threading / z42.net 调用以记录连接 / 调度日志

## 实施期发现

无主要 surprise — 设计直接落地。LogLevel 静态字段访问需要 `LogLevel.TRACE`
（class-qualified），不能裸 `TRACE`（z42 静态字段访问规则，random.md 已记录）。
