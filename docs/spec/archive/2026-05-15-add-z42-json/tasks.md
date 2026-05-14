# Tasks: add z42.json

> 状态：🟢 已完成 | 创建：2026-05-14 | 暂停：2026-05-14 | 恢复 + 完成：2026-05-15
> 类型：feat（新增 stdlib，纯脚本，与 z42.toml 镜像）

## 恢复记录（2026-05-15）

dispatch bug 在 [archive/2026-05-15-fix-instance-method-binding-receiver-aware](../../archive/2026-05-15-fix-instance-method-binding-receiver-aware/) 修复后解封。本 spec 恢复实施：
- 撤掉所有 `Json*` 前缀，恢复 clean API（`Get`/`Set`/`ContainsKey`/`Keys`/`At`/`Add`/`Length`/`Count`）
- `JsonWriter.WriteJsonValue` → `JsonWriter.Write`
- `JsonParser.ParseJsonRoot` → `JsonParser.ParseDocument`
- z42.json 加入 workspace.toml + build-stdlib.sh + index.json
- 加 z42.text 为显式 dep（之前测试时漏的，StringBuilder 用到）
- 全部 53 个 [Test] 全绿，无 regression

实施期补丁不影响（沿用 z42.toml 同款 workaround）：
- 字段集合用 `T[]` + 计数（z42 类字段不支持泛型类型参数）
- 异常类放 `Std` namespace 让 `: Exception` 同 namespace 解析
- Char 比较用 `(int)c` 转换
- 跨文件类返回值用 `(JsonValue)x` 显式 cast

## 暂停原因（2026-05-14）

实现完成（JsonValue / JsonParser / JsonWriter / JsonException 全部写就，44 个 [Test] 全绿当 z42.json 单独运行）。但**与 z42.toml 同时加载时触发跨 zpkg method dispatch bug**：

**症状**：当 z42.json 和 z42.toml 同时加载，TomlParser 内 `target.ContainsKey(seg)` （target 是 TomlValue）的 VCall 解析到 `Std.Collections.Dictionary.ContainsKey` 而非 `Std.Toml.TomlValue.ContainsKey`。然后 Dictionary 的 bytecode 试图调用 `Std.Toml.TomlValue.FindSlot`（Dictionary 的内部方法，TomlValue 没有），报 "function not found"。

**关键发现**：
- 单独运行 z42.toml （workspace 不含 z42.json）：所有测试 ✅ 全绿
- 单独运行 z42.json （workspace 不含 z42.toml）：测试无法 boot（依赖 z42.core）—未单独验过，但 smoke test ok
- z42.toml + z42.json 同时加载：z42.toml 的 stringify / parse 路径回归（dispatch 错位）
- 多次 method 改名（`Get`→`JsonGet`, `Set`→`JsonSet`, `Keys`→`ObjectKeys`, `ContainsKey`→`JsonContainsKey`, `Count`→`JsonCount`, `Length`→`JsonLength`, `At`→`JsonAt`, `Add`→`JsonAdd`, `Write`→`WriteJsonValue`, `ParseDocument`→`ParseJsonRoot`）不解决根因 —— 仅 `Is*` / `As*` / `KindName` 等仍共名

**根因假设**（待验证）：
- 同名 method 的全局函数表 first-loaded 或 last-loaded wins
- 与 fix-cross-pkg-subclass-fields 的 vtable fixup 路径交互
- 或者 introduce-method-token 的 method id 分配在多 zpkg 同名时跨 zpkg 串了

**修复需要 lang/vm 级别 spec**（独立于 stdlib），暂停 z42.json 等待。

## 恢复计划

1. 先开 `fix-cross-pkg-method-dispatch` spec 定位根因：
   - 复现：`new Std.Toml.TomlValue.OfTable().ContainsKey("x")` 在仅 z42.toml 时返回 false（正确），加载 z42.json 后调用 `Std.Toml.TomlValue.ContainsKey` 函数表 entry 改指向 Std.Json 或 Std.Collections 哪个 method
   - 检查 `register_lazy_function` / `function_table.insert` 的冲突策略（first-wins / last-wins）
   - 检查 `introduce-method-token` 的 method id allocation：同名跨 zpkg 是否复用 id？
2. 修复后恢复 z42.json：
   - 把 workspace.toml / build-stdlib.sh 重新加 z42.json
   - 取消方法名 `Json` 前缀（恢复 `Get/Set/ContainsKey/At/Add/Length/Count/Keys` 简短 API）
   - 删除 JsonParser/JsonWriter 的 `Json*Root` / `WriteJsonValue` 前缀
3. 跑完整 stdlib 回归确认

## 已完成的实现（保留在磁盘 / git 未跟踪）

- `src/libraries/z42.json/z42.json.z42.toml` (manifest)
- `src/libraries/z42.json/src/JsonValue.z42` (~230 行，含 Parse/Stringify/StringifyPretty + Of* 工厂 + Is*/As* + 各种带 Json 前缀的实例方法)
- `src/libraries/z42.json/src/JsonException.z42` (在 Std namespace)
- `src/libraries/z42.json/src/JsonParser.z42` (~350 行，含 surrogate pair handling)
- `src/libraries/z42.json/src/JsonWriter.z42` (~150 行，含 compact + pretty 两模式)
- `src/libraries/z42.json/tests/parse_basic.z42` (15 tests)
- `src/libraries/z42.json/tests/parse_strings.z42` (10 tests)
- `src/libraries/z42.json/tests/parse_numbers.z42` (10 tests)
- `src/libraries/z42.json/tests/parse_errors.z42` (9 tests)
- `src/libraries/z42.json/tests/stringify.z42` (9 tests)

共 ~700 行 实现 + 50+ tests。代码保留在工作树中（未 commit），dispatch bug 修复后即可恢复。

---

## 原始进度概览（已搁置）

- [ ] 阶段 1: JsonValue + JsonException 骨架（与 TomlValue 同模式）

## 进度概览

- [ ] 阶段 1: JsonValue + JsonException 骨架（与 TomlValue 同模式）
- [ ] 阶段 2: 解析器（tokenizer + recursive-descent）
- [ ] 阶段 3: stringify（compact + pretty 两模式）
- [ ] 阶段 4: 测试（≥30 个 [Test] 跨 5 文件）
- [ ] 阶段 5: workspace + build-stdlib + docs 同步
- [ ] 阶段 6: GREEN 验证 + 归档 + commit + push

## 阶段 1: 骨架

- [ ] 1.1 NEW `src/libraries/z42.json/z42.json.z42.toml` — manifest
- [ ] 1.2 NEW `src/libraries/z42.json/src/JsonValue.z42`
  - 同 TomlValue 结构（discriminated union + raw arrays + 谓词方法）
  - 静态入口：`Parse(text)` / `Stringify(root)` / `StringifyPretty(root)`
  - 工厂：`OfString` / `OfLong` / `OfDouble` / `OfBool` / `OfNull` / `OfArray` / `OfObject`
  - 谓词：`IsString` / `IsLong` / `IsDouble` / `IsBool` / `IsNull` / `IsArray` / `IsObject` / `KindName`
  - 解构：`AsString` / `AsLong` / `AsDouble` / `AsBool`（null 也有 IsNull 谓词；无 AsNull）
  - 对象操作：`Get(string key)` / `Set(string key, JsonValue v)` / `ContainsKey(string key)` / `Keys()` / `Count()`
  - 数组操作：`Length()` / `At(int i)` / `Add(JsonValue v)` / `Count()`
- [ ] 1.3 NEW `src/libraries/z42.json/src/JsonException.z42`
  - `class JsonException : Exception`（在 `namespace Std`，同 TomlException 模式）
  - 字段 `int Line` / `int Column`

## 阶段 2: 解析器

- [ ] 2.1 NEW `src/libraries/z42.json/src/JsonParser.z42`
- [ ] 2.2 跳过 whitespace（space / tab / newline / CR）
- [ ] 2.3 根值：`_parseValue()` 分流 `{` → object，`[` → array，`"` → string，`t/f/n` → bool/null，digit/`-` → number
- [ ] 2.4 字符串：basic `"..."` + 全 escape 集（`\b \f \n \r \t \" \\ \/`），`\uXXXX` BMP，surrogate pair → 单 char（z42 char 是 32-bit codepoint）
- [ ] 2.5 数字：optional `-`，integer digits（无 leading zero 除非纯 0），optional fraction `.123`，optional exponent `eE[+-]?digits`。解析为 i64 或 f64
- [ ] 2.6 对象：`{ "k": v, ... }`，处理 trailing comma 禁止（RFC 8259 严格）
- [ ] 2.7 数组：`[ v, v, ... ]`
- [ ] 2.8 重复 key 策略：last-wins（与 serde_json 一致）
- [ ] 2.9 整数 overflow：i64 越界 → fallback to f64（lossy）

## 阶段 3: Stringify

- [ ] 3.1 NEW `src/libraries/z42.json/src/JsonWriter.z42`
- [ ] 3.2 Compact 模式：无空格 `{"k":1,"a":[1,2]}`
- [ ] 3.3 Pretty 模式：2-space indent + newline 每元素
- [ ] 3.4 字符串转义反向（确保 `<` / `>` / non-ASCII 默认保留 UTF-8 字节而非 `\uXXXX` —— 与 RFC 8259 一致）

## 阶段 4: 测试

- [ ] 4.1 NEW `tests/parse_basic.z42` —— scalars, simple objects, simple arrays
- [ ] 4.2 NEW `tests/parse_strings.z42` —— 全 escape, unicode, surrogate pairs
- [ ] 4.3 NEW `tests/parse_numbers.z42` —— int / float / exponent / -0 / 边界
- [ ] 4.4 NEW `tests/parse_errors.z42` —— trailing comma, unterminated, bad escape
- [ ] 4.5 NEW `tests/stringify.z42` —— compact + pretty round-trip

## 阶段 5: Wiring + docs

- [ ] 5.1 MODIFY `src/libraries/z42.workspace.toml` —— 加 `"z42.json"`
- [ ] 5.2 MODIFY `scripts/build-stdlib.sh` —— `LIBS` + `"Std.Json": "z42.json.zpkg"`
- [ ] 5.3 NEW `src/libraries/z42.json/README.md`
- [ ] 5.4 NEW `docs/design/stdlib/json.md` —— 设计 + Deferred
- [ ] 5.5 MODIFY `docs/design/stdlib/roadmap.md`
- [ ] 5.6 MODIFY `docs/design/stdlib/organization.md`
- [ ] 5.7 MODIFY `src/libraries/README.md`

## 阶段 6: GREEN + 归档

- [ ] 6.1 `./scripts/build-stdlib.sh` 全绿
- [ ] 6.2 `./scripts/test-stdlib.sh z42.json` 全绿
- [ ] 6.3 `./scripts/test-stdlib.sh` 整体不回归
- [ ] 6.4 mv `docs/spec/changes/add-z42-json/` → `docs/spec/archive/2026-05-14-add-z42-json/`
- [ ] 6.5 commit + push

## 备注

z42.json 与 z42.toml 共享同款 workarounds（已 patch in z42.toml，复用即可）：
- 字段集合用 `T[]` + 计数
- 异常类放 `Std` 命名空间用 `: Exception`
- Char 比较用 `(int)c` 转换
- 跨文件类返回值用 `(JsonValue)x` 显式 cast
- `long.Parse` / `double.Parse` 走 `int.Parse` workaround（z42.toml 实施期把 `long.Parse` 已加进 z42.core）
