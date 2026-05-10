# Tasks: fix-generic-array-type-parsing

> 状态：🟢 已完成 | 类型：fix (parser) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 修复 parser 不识别 `T<U, V>[]` 类型表达式的限制（之前阻塞 Dictionary.Entries() 等返回泛型类数组的 API）。

**根因：** [TypeParser.cs:32-55](src/compiler/z42.Syntax/Parser/TypeParser.cs#L32) 用 `if (LBracket) ... else if (Lt) ...` 互斥结构 —— `T[]` 和 `T<U,V>` 二选一，没处理 `T<U,V>[]` 复合形式。

**修复：** 重构为后缀链（loop 处理 `[]` / `<...>` / `?` 任意组合）。

## Tasks

- [x] 1.1 `TypeParser.cs`：把 `if/else if` 拆开，generic args 之后再检查 `[]` 数组后缀
- [x] 1.2 验证不破坏既有：`T`, `T[]`, `T<U>`, `T<U,V>`, `T?`, `T<U>?`, `T[]?`
- [x] 2.1 `Dictionary.z42`：恢复 `Entries()` 方法（返回 `KeyValuePair<K,V>[]`）
- [x] 2.2 更新 `20_dict_iter` golden test：用 `dict.Entries()` 演示
- [x] 3.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 4.1 commit + push + 归档

## 备注

- 影响：解锁 `dict.Entries()`、其他返回泛型类数组的 API，以及任何 `Pair<K,V>[]` / `Result<T,E>[]` 等用法
- **第二处补丁**：`StmtParser.IsTypeAnnotatedVarDecl` 也只识别 `T`/`T?`/`T[]`/`T?[]`，不识别 `T<U>` / `T<U,V>[]`。重写成 lookahead 扫描：可选 `<...>` (深度计数)、可选 `?`、可选 `[]`，再检查 `Identifier + (= | ;)`
- **后续 backlog**：访问 `entries[m].Value` 拿到的类型是 generic param `V` 不是具体的 `int` —— TypeChecker 对"`T<U,V>[]` 元素的成员访问"未做 type-arg substitution。20_dict_iter 测试只验证 `entries.Length`
