# Tasks: add z42.toml

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-14 | 类型：feat（新增 stdlib，纯脚本，无新 VM/IR）

## 进度概览

- [x] 阶段 1: TomlValue + TomlException 骨架
- [x] 阶段 2: 词法器（tokenizer）
- [x] 阶段 3: 语法分析器（recursive descent + section/AoT 状态）
- [x] 阶段 4: 字符串 / 数字 / 布尔 / 数组 / 内联表细化
- [x] 阶段 5: stringify（值树 → TOML 文本）
- [x] 阶段 6: 测试（42 个 [Test] 跨 5 文件，全绿）
- [x] 阶段 7: workspace + build-stdlib + docs 同步
- [x] 阶段 8: GREEN 验证 + 归档 + commit + push

## 阶段 1: 骨架

- [ ] 1.1 NEW `src/libraries/z42.toml/z42.toml.z42.toml` — manifest（kind="lib"，dep on z42.core）
- [ ] 1.2 NEW `src/libraries/z42.toml/src/TomlValue.z42`
  - 私有字段：`_kind: int`、`_str / _long / _double / _bool / _array: List<TomlValue> / _table: Dictionary<string, TomlValue>`
  - kind 常量用 `Is*()` 谓词方法（**不暴露**静态字段，绕过 z42 static field read-path 限制）
  - 工厂：`OfString(s)` / `OfLong(n)` / `OfDouble(d)` / `OfBool(b)` / `OfArray()` / `OfTable()`
  - 谓词：`IsString()` / `IsLong()` / `IsDouble()` / `IsBool()` / `IsArray()` / `IsTable()`
  - 访问器（错类型抛 TomlException）：`AsString() / AsLong() / AsDouble() / AsBool() / AsArray() / AsTable()`
  - 表操作：`Get(string key) → TomlValue?` / `ContainsKey(string key) → bool` / `Set(string key, TomlValue v)`
  - 数组操作：`Length()` / `At(int i)` / `Add(TomlValue v)`
- [ ] 1.3 NEW `src/libraries/z42.toml/src/TomlException.z42` — `class TomlException : Exception`（含 `Line` / `Column` 字段，重载 ctor 取 message + 行列）

## 阶段 2: Tokenizer

- [ ] 2.1 NEW `src/libraries/z42.toml/src/TomlParser.z42` — 内部 tokenizer + parser 同文件
  - Token kinds（int 常量）：`LBRACKET`、`RBRACKET`、`LBRACE`、`RBRACE`、`EQUALS`、`COMMA`、`DOT`、`IDENT`、`STRING`、`INT`、`FLOAT`、`TRUE`、`FALSE`、`NEWLINE`、`EOF`
  - Tokenizer 跟踪 `line` / `col` 用于错误报告
  - 跳过 `#` 到行尾的注释
  - 字符串解析：basic `"..."` 含 `\n \t \" \\ \uXXXX`，literal `'...'` 不解析转义

## 阶段 3: Parser (核心结构)

- [ ] 3.1 顶层循环：消费 NEWLINE / 注释 → key/value 行 / `[section]` / `[[array-of-tables]]`
- [ ] 3.2 `[a.b.c]` 切换当前活跃表为 `root.a.b.c`（必要时创建中间表；冲突报错）
- [ ] 3.3 `[[exe]]` array-of-tables：root.exe = array, 追加新 table；后续 k/v 写入新 table
- [ ] 3.4 重复 key / 重复 section 校验（TOML 1.0 "Keys" 段）

## 阶段 4: Value 解析

- [ ] 4.1 数字：先 tokenizer 把数字字符（含 `-+0-9.eE`）扫成 raw token，parser 按是否含 `.eE` 分流到 INT/FLOAT
- [ ] 4.2 数组：`[ v, v, v ]`，允许嵌套数组、跨行（TOML 1.0 允许数组内换行）
- [ ] 4.3 内联表：`{ k = v, k = v }`（同行不可换行 — TOML 1.0 强约束）
- [ ] 4.4 `nan` / `inf` / `+inf` / `-inf` 浮点字面量

## 阶段 5: Stringify

- [ ] 5.1 NEW `src/libraries/z42.toml/src/TomlWriter.z42` —内部 stringify 引擎
  - top-level k/v 先输出
  - 然后按字典顺序输出 `[section]` 块
  - 数组按 `[v, v]` 单行（短）或多行（长，>80 字符触发）
  - 字符串：basic 转义优先；含 `"` 时切到 literal `'...'`；都不行则 hex escape
  - 数字按值类型输出（i64 直接，f64 至少含一个 `.`）

## 阶段 6: 测试

- [ ] 6.1 NEW `tests/parse_basic.z42` — ≥10 个 [Test]：顶层 k/v、`[section]`、嵌套 `[a.b]`、整数、浮点、布尔、字符串
- [ ] 6.2 NEW `tests/parse_array.z42` — ≥6 个 [Test]：数组、嵌套数组、内联表、`[[aot]]`、跨行数组
- [ ] 6.3 NEW `tests/parse_strings.z42` — ≥5 个 [Test]：basic / literal、转义、unicode `\uXXXX`
- [ ] 6.4 NEW `tests/parse_errors.z42` — ≥5 个 [Test]：重复 key、悬空 `=`、未闭合 `[` / `"` / `{`、坏数字 → TomlException 带行列信息
- [ ] 6.5 NEW `tests/stringify.z42` — ≥4 个 [Test]：round-trip 简单 case、数组格式、字符串转义、value-only API

## 阶段 7: Wiring + docs

- [ ] 7.1 MODIFY `src/libraries/z42.workspace.toml` — `default-members` 加 `"z42.toml"`
- [ ] 7.2 MODIFY `scripts/build-stdlib.sh` — `LIBS` 加 `z42.toml` + index.json 加 `"Std.Toml": "z42.toml.zpkg"`
- [ ] 7.3 MODIFY `src/libraries/README.md` — 包列表加 z42.toml 行
- [ ] 7.4 MODIFY `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` — 加 z42.toml 缓存断言
- [ ] 7.5 NEW `src/libraries/z42.toml/README.md` — 包目录 README
- [ ] 7.6 NEW `docs/design/stdlib/toml.md` — 设计文档（架构 + Decision + Deferred 段）
- [ ] 7.7 MODIFY `docs/design/stdlib/roadmap.md` — z42.toml 从 P2 移到 "已落地"，更新现状回顾；Deferred Backlog Index 加 datetime / multiline / hex-int 等
- [ ] 7.8 MODIFY `docs/design/stdlib/organization.md` — 现状包列表加 z42.toml 行

## 阶段 8: GREEN + 归档

- [ ] 8.1 `./scripts/build-stdlib.sh` 全绿
- [ ] 8.2 `./scripts/test-stdlib.sh z42.toml` 全绿
- [ ] 8.3 `./scripts/test-stdlib.sh` 全绿（其他 lib 不回归）
- [ ] 8.4 spec scenarios 覆盖确认（按 proposal.md "What Changes"）
- [ ] 8.5 mv `docs/spec/changes/add-z42-toml/` → `docs/spec/archive/2026-05-14-add-z42-toml/`
- [ ] 8.6 commit: `feat(stdlib): add z42.toml — TOML 1.0 subset reader/writer`
- [ ] 8.7 push origin main

## 实施期补丁 / scope 内副作用

1. **`long.Parse` 添加到 z42.core**（in-scope，1 行）— `__long_parse` builtin 已存在但无 z42 wrapper；TOML 整数 parse 需要。位置 `src/libraries/z42.core/src/Primitives/Long.z42:5`。
2. **TomlException 放 `namespace Std;` 而非 `Std.Toml;`** —— 为了 `: Exception` 通过同 namespace lookup 正确解析（`Std.Toml.Exception` 会被当作未定义函数）。其余类（TomlValue / TomlParser / TomlWriter）保留 `Std.Toml`。同 ProcessStartException 模式。
3. **Workarounds for known z42 compiler limitations**（已 patch，无需后续修复）：
   - `out` 是 z42 keyword（ref/out/in），改用 `result` / `s` 命名
   - 字段类型 `List<T>` / `Dictionary<K, V>` 的泛型参数被 parser 静默丢弃，全改为 `T[]` + 计数（同 Process.z42 pattern）
   - 字符比较 `c >= 'A'` 不支持（VM Char vs Char comparison），改为 `(int)c >= 65` 模式（同 z42.encoding pattern）
   - 跨文件类引用：`return parser.ParseDocument()` 返回 `TomlValue`，类型 checker 报 "TomlValue ≠ TomlValue" mismatch。workaround：显式 `return (TomlValue)parser.ParseDocument();`（identity cast 走 convert_value 的 reference-type pass-through —— 我之前 add-std-process 修复的路径）
4. **TomlValue 既是值类型也是入口点**：原计划单独 `Toml.z42` facade 暴露 `Parse` / `Stringify`，但类 `Toml` 名与 namespace `Toml` 在 lazy_loader 里冲突。最终把 `Parse` / `Stringify` 作为 `TomlValue` 的静态方法。API 仍清晰：`TomlValue.Parse(text)` / `TomlValue.Stringify(root)`。

## 备注

### 关键设计选择（vs 完整 design.md）

skip 单独 design.md — 关键决策直接列在 proposal.md 和本 tasks.md：

1. **TomlValue 用谓词方法而非暴露 static int KIND_* 常量** —— 绕开 z42 当前 static field read-path 返回 0 default 的限制（参 add-std-process tasks.md 实施期补丁 #3 / Stdio.z42 的同类 workaround）。
2. **数字分 long / double 两个槽位**（不统一为 double）—— TOML 整数是 i64，浮点是 f64，分开存避免 `2^53+` 的整数精度丢失。
3. **AoT (`[[arr]]`) 实现策略**：tokenizer 区分 `[[` 和 `[`；parser 见 `[[name]]` 时 ensure `root.name` 是 array，追加新空 table。后续 `key = value` 写入"当前 AoT 元素"。
4. **错误恢复**：不做（v0 fail-fast，遇到第一个错误抛 TomlException 即返回）。多错误聚合留 future（依赖 ErrorList 设计）。
