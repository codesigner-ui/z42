# Tasks: z42.text 扩 Strings 工具集（review.md S3 / S5 Phase 1）

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：feat（新 stdlib API）
> 来源：[`docs/review.md`](../../../review.md) Part 3 S3 / S5 "z42.text 扩充"

## 变更说明

z42.text 当前仅 `StringBuilder` + `Levenshtein`（159 LOC）。review.md S3
评 "太单薄" + S5 列 "Format / Tokenize / Splitter / Padding helpers" 缺失。
本 spec Phase 1 加 `Std.Text.Strings` static class 提供 5 个 universally-useful
helpers：

| 方法 | 用途 |
|---|---|
| `PadLeft(s, width, fill)` | 左侧填充到宽度 |
| `PadRight(s, width, fill)` | 右侧填充 |
| `Repeat(s, count)` | 字符串重复 N 次 |
| `IndexOfAny(s, chars)` | 找第一个匹配任一字符的位置 |
| `TrimChars(s, chars)` | 用任意字符集合 trim |

全 pure script（基于 `Std.String.Length / CharAt / FromChars / Substring`），
无 extern 依赖。各 method 顶部块注释说明语义 / 复杂度 / 边界。

## 原因

review.md S5 把 z42.text 扩充列为 S-P2（5-7 天）。完整版（Format /
Tokenize / Splitter）需求各自不轻。本 Phase 1 切片：**只 ship "明显缺
且无依赖" 的 5 个 helpers**，给 z42.text 立刻拿到一个可用工具集；Format
（字符串插值类） / Tokenize / Splitter 等独立 spec 后续。

## 文档影响

- z42.text/README.md 加 `Strings` 类介绍 + 用法 example
- `docs/review.md` S3 / S5 状态更新

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/libraries/z42.text/src/Strings.z42` | NEW | static class with 5 helpers |
| `src/libraries/z42.text/tests/strings.z42` | NEW | unit tests with [Test] |
| `src/libraries/z42.text/README.md` | MODIFY | 加 Strings 节 |
| `docs/review.md` | MODIFY | S3 / S5 状态更新 |

只读引用：`src/libraries/z42.core/src/String.z42` — Length / CharAt /
Substring / FromChars 使用。

## 设计要点

### O(n) 字符遍历

`String.CharAt(int)` 是 O(n) UTF-8 解码（review.md C3 说过 length is
O(n) char-count）。这里的 helper 仍然 O(n²) worst case（PadLeft 一次
walk + Repeat 嵌套循环 O(srcLen * count) chars × CharAt O(srcLen)）。
真正 O(1) char access 等 z42.core String 引入 char[] cache 或 ByteLength
切换语义后再优化（独立 spec）。Phase 1 接受当前 O(...) 配置。

### Method 命名

不用 `String.PadLeft` instance method 而走 `Strings.PadLeft(s, ...)` 静态：
- BCL 风格（C# `Microsoft.Extensions.Primitives.StringValues` 等）
- 不需扩张 z42.core 的 `String` 类（z42.core 是 prelude，每加一个方法都
  pollute 全局 namespace）
- z42.text 是显式 import 包，加 API 在那里更合适

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `z42.text/src/Strings.z42` —— 5 个 helper + 1 private helper (ContainsChar)
- [x] 1.2 NEW `z42.text/tests/strings.z42` —— 20 [Test] 覆盖边界
- [x] 1.3 MODIFY `z42.text/README.md` 加 Strings 介绍
- [x] 1.4 VERIFY `./scripts/test-stdlib.sh z42.text` 20/20 + 现有 z42.text 13/13 全过
- [x] 1.5 MODIFY `review.md` S3 + S5 + 总优先级表 (P5 项变 🟡 partial)
- [x] 1.6 归档 + commit + push
