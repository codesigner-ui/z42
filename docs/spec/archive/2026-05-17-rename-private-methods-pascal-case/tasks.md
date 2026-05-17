# Tasks: rename private methods from `_camelCase` to `PascalCase`

> 状态：🟢 已完成 | 创建：2026-05-17 | 完成：2026-05-17 | 类型：refactor
> Spec 类型：minimal mode

**变更说明**：把 stdlib 全部 `_camelCase` 私有方法 rename 为 `PascalCase`，严格遵守 [naming-conventions.md](../../../design/language/naming-conventions.md) 规则 2 + 规则 4。

**原因**：早期 stdlib 把 `_` 当通用"私有"前缀同时用在字段和方法上，但规范明文：
- 规则 2：「公开方法 `public int Sum()` 与私有方法 `private int Sum()` 都用 `PascalCase`」例子无下划线
- 规则 4：`_` 前缀**仅**为私有字段保留，rationale 是字段和局部变量同走 camelCase 易混淆
- 私有方法不存在这种混淆（`this.foo()` 永不被当局部变量），所以规范不给私有方法保留 `_`

**附带两条独立真违规**：
- z42.diagnostics LogLevel：`public static int TRACE / DEBUG / INFO / WARN / ERROR` → `Trace / Debug / Info / Warn / Error`（规则 11 + 规则 7：公开静态字段 = PascalCase）
- z42.uri UriCodec：`private static string HEX` → `_hex`（规则 11：私有静态字段 = `_camelCase`，同私有实例字段）

**Scope**（rename surface 内全部 private 方法 + 上述两条）：

| 文件 | 私有方法数 |
|------|--------|
| `src/libraries/z42.uri/src/Uri.z42` | 7 |
| `src/libraries/z42.io/src/Process.z42` | 2 |
| `src/libraries/z42.io/src/ProcessHandle.z42` | 3 |
| `src/libraries/z42.io/src/Exceptions/ProcessExitException.z42` | 1 |
| `src/libraries/z42.cli/src/ArgParser.z42` | 10 |
| `src/libraries/z42.json/src/JsonParser.z42` | 15 |
| `src/libraries/z42.json/src/JsonWriter.z42` | 7 |
| `src/libraries/z42.random/src/Random.z42` | 2 |
| `src/libraries/z42.io.binary/src/BinaryReader.z42` | 1 |
| `src/libraries/z42.io.binary/src/BinaryWriter.z42` | 1 |
| `src/libraries/z42.encoding/src/Hex.z42` | 1 |
| `src/libraries/z42.diagnostics/src/Log.z42` | 1 |
| `src/libraries/z42.diagnostics/src/LogLevel.z42` | (5 公开常量大小写) |
| `src/libraries/z42.regex/src/Regex.z42` | 8 |
| `src/libraries/z42.regex/src/RegexParser.z42` | 12 |
| `src/libraries/z42.regex/src/Match.z42` | ? |
| `src/libraries/z42.toml/src/TomlWriter.z42` | 9 |
| `src/libraries/z42.toml/src/TomlParser.z42` | ~20 |

**Out of Scope**：
- 私有字段（保持 `_camelCase` —— 规则 4 不动）
- 公开方法 / 公开字段（保持 PascalCase 不动）
- 调用方（私有方法不可能被 lib 外引用；callsite 改动全在同文件内）
- naming-conventions.md 不改（规范自洽）

**文档影响**：无（这是机械式纠正违规，不改规范、不改公开 API、不改外部可见行为）

## Tasks

- [x] 1.1 z42.uri/src/Uri.z42 — 7 个方法 + UriCodec.HEX
- [x] 1.2 z42.io/src/Process.z42 — 2 个方法
- [x] 1.3 z42.io/src/ProcessHandle.z42 — 3 个方法
- [x] 1.4 z42.io/src/Exceptions/ProcessExitException.z42 — 1 个方法
- [x] 1.5 z42.cli/src/ArgParser.z42 — 10 个方法
- [x] 1.6 z42.json/src/JsonParser.z42 — 15 个方法
- [x] 1.7 z42.json/src/JsonWriter.z42 — 7 个方法
- [x] 1.8 z42.random/src/Random.z42 — 2 个方法
- [x] 1.9 z42.io.binary/src/BinaryReader.z42 — 1 个方法
- [x] 1.10 z42.io.binary/src/BinaryWriter.z42 — 1 个方法
- [x] 1.11 z42.encoding/src/Hex.z42 — 1 个方法
- [x] 1.12 z42.diagnostics/src/Log.z42 — 1 个方法
- [x] 1.13 z42.diagnostics/src/LogLevel.z42 — 5 个常量大小写
- [x] 1.14 z42.regex (3 files) — 20+ 个方法
- [x] 1.15 z42.toml (2 files) — 30+ 个方法
- [x] 1.16 build-stdlib.sh 全绿
- [x] 1.17 test-stdlib.sh 不回归
- [x] 1.18 commit + push（含本 spec + .claude/）
- [x] 1.19 mv → docs/spec/archive/2026-05-17-rename-private-methods-pascal-case/

## 备注

实施期发现的问题写这里：

—
