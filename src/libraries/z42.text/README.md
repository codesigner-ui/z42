# z42.text — 文本处理库

## 职责

z42 文本处理类型。**纯脚本实现** —— 严格遵循 [`src/libraries/README.md`](../README.md)
"VM 接口集中在 z42.core" 规则，本包不声明任何 `[Native(...)] extern` 方法。

## src/ 核心文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `StringBuilder.z42` | `StringBuilder` | 字符串拼接缓冲区 — Script-First 实现，基于 `string[]` + `String.FromChars` |

> **Regex 在 [`z42.regex`](../z42.regex/)** —— 不在本包。本包的 `Regex.z42` 旧 stub 已删除（commit 2026-05-24 docs/review.md Part 3 S2.2 清理）。

## 实现备注

`StringBuilder` 内部用 `string[]` 收集 Append 片段（按 2× 扩容），ToString 时
一次性合并到 `char[]` 再 `String.FromChars` 出来。不用 `List<string>` 是因为
parser 当前对字段声明的泛型实例化语法（`List<string> _parts;`）会误识别为
method header；待后续 parser 修复后可以切换。
