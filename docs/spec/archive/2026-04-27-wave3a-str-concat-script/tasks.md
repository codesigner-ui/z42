# Tasks: wave3a-str-concat-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 3a：`string.Concat(string, string)` + `string.Concat(string, string, string)` 两个 [Native] 重载迁纯脚本 `return a + b;`，删除 `__str_concat` builtin。

**前提确认：** 编译器对 `"hello" + "world"` 已使用专门的 `StrConcatInstr` IR 指令（见 [FunctionEmitterCalls.cs:210 EmitConcat](src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs#L210)），与 `__str_concat` builtin 不共享路径。`__str_concat` 只被 stdlib 的 `string.Concat(...)` 静态方法调用。

**原审计表分类**：⚫ Wave 3，"需要新基础设施"。实测无需 — `+` 已是 IR 指令，脚本直接 `return a + b;` 一行解决。归并简化。

## Tasks

- [x] 1.1 `String.z42`：2 个 Concat overload 改 `return a + b;` / `return a + b + c;`
- [x] 2.1 `corelib/mod.rs`：删 `__str_concat` dispatch + 注释
- [x] 2.2 `corelib/string.rs`：删 `builtin_str_concat`
- [x] 3.1 `src/libraries/README.md` 审计表：str_concat ✅；当前总计 ~45 → ~44
- [x] 4.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 5.1 commit + push + 归档

## 备注

- 既有 44_string_static_methods 测试覆盖 `string.Concat(...)` 行为，足够锁定
- `value_to_str` 隐式转换（原 Rust builtin 容许 non-string 参数）丢失 —— 但 [Native] 声明本来就限定 string，调用者已不能传非 string，此为无副作用退化
