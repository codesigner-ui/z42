# Proposal: 将 String 方法迁移到脚本实现

## Why

当前 `String.z42` 有 22 个 `[Native("__str_*")]` extern 方法，VM 侧对应 22 个 `builtin_str_*` 函数。大多数方法（`Contains` / `StartsWith` / `IndexOf` / `Trim` / `Substring` / `Replace` / `ToLower` / `ToUpper` 等）本质是"循环字符 + 比较"，可以用脚本实现，却占据了 extern 预算，违反 Simplicity-First / Script-First / per-package extern 预算规则。

同时现状存在语义不一致：`Length` 按 Unicode scalar 计数（`s.chars().count()`），但 `Substring` / `IndexOf` 按 UTF-8 byte 偏移（`&s[start..end]`、`s.find(sub)`）。这也源于"每个操作独立 builtin"的结构。

C# BCL 的做法是参考样板：`string` 仅 `Length` / `this[int]` 是 intrinsic（由 JIT 展开为 ldfld），`Contains` / `StartsWith` / `IndexOf` / `Trim` / `Substring` 全部是 C# 代码循环字符实现。我们对齐这个模式。

## What Changes

- 引入**最小字符串核心**：保留 `Length` extern；新增 `CharAt(int) -> char` 和 `FromChars(char[]) -> string` extern；保留 `Equals` / `CompareTo` / `GetHashCode` / `Format` / `Split` extern（性能 / 复杂度原因）
- 扩展**最小字符核心**：`Char.z42` 新增 `IsWhiteSpace()` / `ToLower()` / `ToUpper()` 三个 extern，服务 `string.Trim` / `string.ToLower` / `string.ToUpper` 脚本实现
- 以下 String 方法改为**纯脚本实现**：`IsEmpty` / `Contains` / `StartsWith` / `EndsWith` / `IndexOf` / `Replace` / `Substring(start)` / `Substring(start, length)` / `ToLower` / `ToUpper` / `Trim` / `TrimStart` / `TrimEnd` / `IsNullOrEmpty` / `IsNullOrWhiteSpace`
- 统一语义：所有索引 / 长度 / 切片一律按 **Unicode scalar (char)** 计数；UTF-8 byte 视图不再从 String 对外暴露
- VM builtin 净变化：删除 14 个 `__str_*`，新增 2 个 `__str_*` + 3 个 `__char_*`，净减少 9 个 extern

## Scope

| 文件 / 模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/libraries/z42.core/src/String.z42` | 重写 | extern 降至 ~7 个，其余改脚本 |
| `src/libraries/z42.core/src/Char.z42` | 扩展 | 新增 3 个 extern 方法 |
| `src/runtime/src/corelib/string.rs` | 删减 + 新增 | 删 11 个 builtin（1 个双签名合计 13 条），新增 2 个 |
| `src/runtime/src/corelib/` | 新增 | `char.rs` 或在现有文件加 `__char_*` builtin |
| `src/runtime/src/corelib/mod.rs` | 同步 | dispatch 表注册 / 注销 |
| `src/libraries/z42.core/README.md` | 同步 | 反映新的 extern 集合 |
| `src/runtime/tests/golden/run/NN_string_script/` | 新增 | 端到端覆盖全部重写方法 |
| `src/compiler/z42.Tests/` | 新增 | golden 覆盖每个重写方法 |

## Out of Scope

- 不引入 `s[i]` 字符串索引语法糖（用显式 `s.CharAt(i)`，后续可单独做 `add-string-indexer`）
- 不引入 `0..n` range 语法（循环用 C-style `for (int i = 0; i < n; i++)`）
- 不把 `string` 改为 struct（`Value::Str` 在 VM 中保持 primitive）
- 不脚本化 `Split` / `Format` / `Join` / `Concat`（char[] 分配 / 变参 / 格式串解析较复杂，本迭代保留）
- 不处理 locale-sensitive casing（`ToLower` / `ToUpper` 用 ASCII 规则，对齐 C# `InvariantCulture` 的简化版）

## Open Questions

- [ ] 现有 `.Substring` / `.IndexOf` callers 是否会被 byte → char 语义变更影响？—— 当前 callers 全是 ASCII 字符串，不受影响；本迭代保持等价
- [ ] `__str_compare_to` 当前位于 `corelib/convert.rs` 而非 `string.rs`，是否要顺手搬家？—— 不搬，避免 scope 蔓延
