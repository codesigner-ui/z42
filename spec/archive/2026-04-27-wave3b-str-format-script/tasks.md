# Tasks: wave3b-str-format-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 3b（Wave 3 收官）：`string.Format(string, object)` + `string.Format(string, object, object)` 两个 [Native] 重载迁纯脚本，删除 `__str_format` builtin。

**实测无需 IFormattable**：原 Rust builtin 只做 `{0}` / `{1}` 字面替换（不识别 `{0:F2}` 等格式说明符）。脚本可直接用现有 `string.Replace(string, string)` + `Convert.ToString(object)`：

```z42
public static string Format(string format, object arg0) {
    return format.Replace("{0}", Convert.ToString(arg0));
}
public static string Format(string format, object arg0, object arg1) {
    return format
        .Replace("{0}", Convert.ToString(arg0))
        .Replace("{1}", Convert.ToString(arg1));
}
```

`Convert.ToString` 仍然走 `__to_str` builtin（保留），它对所有 Value 变体（int / null / object 等）做 value_to_str 转换。

**不引入 IFormattable**：未来若要支持 `{0:F2}` 等格式说明符，再独立 spec 引入 `IFormattable` 协议 + 重写 Format 实现。

## Tasks

- [x] 1.1 `String.z42`：2 个 Format overload 改纯脚本（Replace + Convert.ToString 链）
- [x] 2.1 `corelib/mod.rs`：删 `__str_format` dispatch + 注释
- [x] 2.2 `corelib/string.rs`：删 `builtin_str_format`
- [x] 3.1 `src/libraries/README.md` 审计表：str_format ✅；当前总计 ~44 → ~43
- [x] 4.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 5.1 commit + push + 归档

## 备注

- 既有 44_string_static_methods 测试覆盖 `string.Format("{0} is {1}", "age", 42)`
- **顺序替换语义**：与原 Rust 等价（如果 arg0 含 "{1}" 字面量会被后续 replace 误处理 —— 与原 builtin 同 bug，不修）
