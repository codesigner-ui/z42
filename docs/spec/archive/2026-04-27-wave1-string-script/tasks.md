# Tasks: wave1-string-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 1.5（Wave 1 收官）：`string.Split` + `string.Join` 两个 overload 从 `[Native]` 迁纯脚本，删除 `__str_split` + `__str_join` 2 个 builtin。

**原因：** BCL `string.Split` / Rust `str::split` 都是脚本/源码（C# 用 Span 扫，Rust 是 iterator）。z42 已具备 `__str_char_at` + `Substring`（脚本）+ `__str_from_chars` + `for` 循环，足够实现。

**保留**：`__str_concat`（可能后续走 codegen 特化）+ `__str_format`（依赖 `IFormattable` 协议；Wave 3+ 处理）。

## Tasks

- [x] 1.1 重写 `src/libraries/z42.core/src/String.z42`：`Split(string)` + `Join(string, string[])` + `Join(string, string, string, string)` 三个 [Native] 改脚本
- [x] 2.1 `src/runtime/src/corelib/mod.rs`：删 `__str_split` + `__str_join` dispatch + 注释
- [x] 2.2 `src/runtime/src/corelib/string.rs`：删 `builtin_str_split` + `builtin_str_join`
- [x] 3.1 `src/libraries/README.md` 审计表：split/join 行 🟡 → ✅；Wave 1 标记完成；汇总
- [x] 4.1 `build-stdlib.sh` + `cp dist/*.zpkg → artifacts/z42/libs/`
- [x] 4.2 `regen-golden-tests.sh`、`dotnet test`、`test-vm.sh` 全绿（含既有 14_string_methods + 44_string_static_methods）
- [x] 5.1 commit + push + 归档

## 备注

- 等价行为锁定：
  - `"a,b,c".Split(",")` → `["a", "b", "c"]`
  - `"".Split(",")` → `[""]`
  - `",".Split(",")` → `["", ""]`
  - `"a,".Split(",")` → `["a", ""]`
  - 空 separator → throw new Exception（与 Rust panic 等价）
- `Join(sep, [])` → `""`；`Join(sep, [single])` → `single`
- 实现采用两遍扫描（与 String.Replace 同模式）：先数 split 个数，再分配 string[]
