# Tasks: String.Split(separator, options) — empty/trim filtering

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** 给 `Std.String.Split` 添加 `(separator, options)` 重载，新增 `Std.SplitOptions` 常量类（`None=0` / `RemoveEmptyEntries=1` / `TrimEntries=2`，bitwise OR 组合）。

**原因：** scripts 移植反复需要"split 后扔掉空段 + trim 每段"，目前调用方手写过滤循环。BCL `string.Split(sep, StringSplitOptions)` 等价。

**类型：** 最小化（pure z42 stdlib，无新 Rust native，无 lang change）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/SplitOptions.z42` | NEW | `Std.SplitOptions` static class — None/RemoveEmptyEntries/TrimEntries 三个 int 常量 |
| `src/libraries/z42.core/src/String.z42` | MODIFY | 加 `Split(string separator, int options)` 重载；既有 `Split(string separator)` 不动 |
| `src/libraries/z42.core/tests/split_options.z42` | NEW | 7 [Test]：RemoveEmptyEntries / TrimEntries / 二者组合 / None 与不带 options 等价 / 全空 / 全 whitespace / multi-char separator |
| `src/libraries/z42.core/README.md` | MODIFY | 文档新增 API |

**只读引用：**
- BCL `StringSplitOptions` enum 行为参考
- `scripts/audit-missing-usings.z42` — 现实里 `String.Split("\n")` 后过滤空段的 pattern

## Tasks

- [x] 1.1 NEW `SplitOptions.z42`：`Std` namespace + `public static class SplitOptions { public static int None=0; RemoveEmptyEntries=1; TrimEntries=2; }`
- [x] 1.2 在 `String.z42` 加 `public string[] Split(string separator, int options)`：
  - 复用现有 pass-1 + pass-2 提取
  - 提取完成后按 options 后处理：
    - 若 `(options & TrimEntries) != 0` → 每段 `Trim()`
    - 若 `(options & RemoveEmptyEntries) != 0` → 过滤 `Length == 0`
  - 返回紧凑数组（不是固定长度 + null padding）
- [x] 1.3 写 `tests/split_options.z42` 7 [Test]
- [x] 1.4 更新 `z42.core/README.md` String 段
- [x] 1.5 验证：4 个 manual smoke 全过（z42c 编译 + interp 跑）：`RemoveEmptyEntries` 3 段、`TrimEntries` 3 trim 段、组合 2 段、`None` 等价单参 3 段。**test-runner 仍阻塞**，端到端 GREEN 由后续 session 自动收
- [x] 1.6 归档 + commit + push

## 备注

- 不引入 enum 语法（z42 暂无 `enum` keyword），用 static class + int 常量，跟 `SeekOrigin` / `AlgoId` 同模式。
- `Trim` 已在 String.z42 line 175 实现；直接复用。
- 不引入 `char[] separators` 多分隔符 / `int count` limit 形式，复杂度收窄；后续按需独立 spec。
- 不强行重构既有 `Split(string)` 走 options 路径（保持单参 fast path 不增加分支）。
