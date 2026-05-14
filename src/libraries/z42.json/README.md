# z42.json

## 职责
JSON RFC 8259 reader / writer。覆盖 7 个 value 类型（null / bool / number / string / array / object），含完整 string escape 集合（\b \f \n \r \t \" \\ \/ \uXXXX BMP + \uXXXX\uXXXX surrogate pair），compact + pretty 两种 stringify 模式。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/JsonValue.z42` | 值类型 + 公开入口（`Parse` / `Stringify` / `StringifyPretty` / `Of*` 工厂 / `Is*` / `As*` / 容器操作） |
| `src/JsonException.z42` | `JsonException : Std.Exception`，带 1-based line / column |
| `src/JsonParser.z42` | 内部 recursive-descent parser + tokenizer，含 surrogate-pair 处理 |
| `src/JsonWriter.z42` | 内部 stringifier（compact + pretty 两模式） |

## 入口点
- `Std.Json.JsonValue.Parse(text)` → `JsonValue` (any root value)
- `Std.Json.JsonValue.Stringify(v)` → `string` (compact)
- `Std.Json.JsonValue.StringifyPretty(v)` → `string` (2-space indent)
- `Std.Json.JsonValue.{OfNull, OfBool, OfLong, OfDouble, OfString, OfArray, OfObject}` 构造器
- `Std.Json.JsonValue.{IsNull, IsBool, IsLong, IsDouble, IsString, IsArray, IsObject, KindName}` 谓词
- `Std.Json.JsonValue.{AsBool, AsLong, AsDouble, AsString}` 解构
- `Std.Json.JsonValue.{Get, Set, ContainsKey, Keys}` 对象操作
- `Std.Json.JsonValue.{Length, At, Add, Count}` 数组操作
- `Std.JsonException` 异常类（`Line` / `Column` 字段）

## 用法

```z42
using Std.Json;
using Std;

var v = JsonValue.Parse("{\"name\":\"foo\",\"a\":[1,2,3]}");
v.Get("name").AsString();      // "foo"
v.Get("a").Length();           // 3
v.Get("a").At(1).AsLong();     // 2

var root = JsonValue.OfObject();
root.Set("users", JsonValue.OfArray());
root.Get("users").Add(JsonValue.OfString("alice"));
JsonValue.Stringify(root);              // compact: {"users":["alice"]}
JsonValue.StringifyPretty(root);        // 2-space indented
```

## 依赖关系
依赖 `z42.core` + `z42.text`（StringBuilder for stringify 缓冲）。无其他 stdlib 依赖。

## 与 z42.toml 的关系
两个包并用没问题（仅在 fix-instance-method-binding-receiver-aware 修复后正确）—— 之前因为 method-name dispatch bug 互相干扰，已在 2026-05-15 修复。
