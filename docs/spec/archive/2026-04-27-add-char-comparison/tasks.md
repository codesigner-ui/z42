# Tasks: add-char-comparison

> 状态：🟢 已完成 | 类型：fix (lang/typecheck 扩展) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** TypeChecker 接受 char 类型作为 `<` / `<=` / `>` / `>=` 的操作数（按 codepoint 比较），消除 Wave 2 时 char.CompareTo 不得不绕走 GetHashCode() 的尴尬。

**根因：** [BinaryTypeTable.cs:53-56](src/compiler/z42.Semantics/TypeCheck/BinaryTypeTable.cs#L53) 关系运算符要求 `Numeric` 操作数，而 char 在 [TypeRegistry](src/compiler/z42.Semantics/TypeCheck/TypeRegistry.cs) 标注 `IsNumeric=false`。

**修复：** 引入 `IsOrderable` predicate（numeric || char），用于关系运算符；不影响算术运算符（`+` `-` 等仍只接受 numeric，char 不变）。

## Tasks

- [x] 1.1 `Z42Type.cs`：加 `IsOrderable(t)` = `IsNumeric(t) || t == Char`
- [x] 2.1 `BinaryTypeTable.cs`：4 个关系运算符规则改用 `Orderable` predicate（替代 `Numeric`）
- [x] 3.1 `Char.z42`：CompareTo 简化回 `if (this < other) return -1; ...` 直接用 char 比较
- [x] 4.1 build-stdlib + regen + dotnet test + test-vm 全绿（含 wave2 的 char.CompareTo 行为）
- [x] 5.1 commit + push + 归档

## 备注

- IR 层：char 在 VM 内是 i32 codepoint，比较走 IR `Lt` 等指令（与 int 同路径），无需改 codegen
- 不引入新 builtin
