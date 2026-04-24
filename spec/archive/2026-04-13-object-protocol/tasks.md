# Tasks: Object 基础类型协议

> 状态：🟢 已完成 | 创建：2026-04-13

**变更说明：** ToStr 指令走虚方法分发；string 增加 ToString/Equals/GetHashCode override
**原因：** 用户 override ToString() 后，字符串插值和 Console.WriteLine 不生效
**文档影响：** docs/design/ir.md（ToStr 语义 + String ops section）、docs/design/jit.md（jit_to_str 签名）

- [x] 1.1 VM interp: `ToStr` 对 `Value::Object` 走 vtable `ToString` 分发，fallback 到 `value_to_str`
- [x] 1.2 corelib/string.rs: 新增 `builtin_str_to_string`、`builtin_str_equals`、`builtin_str_hash_code`
- [x] 1.3 corelib/mod.rs: 注册 `__str_to_string`、`__str_equals`、`__str_hash_code`
- [x] 1.4 stdlib String.z42: 新增 `ToString()`、`Equals(object? other)`、`GetHashCode()` override
- [x] 1.5 golden test: `override string ToString()` → 字符串插值生效（interp + jit）
- [x] 1.6 dotnet test (385) + test-vm.sh (94) 全绿
- [x] 1.6b JIT: jit_to_str 增加 ctx 参数支持 vtable ToString 分发；jit_vcall 支持 primitive VCall
- [x] 1.7 docs/design/ir.md 新增 String Operations section（to_str 语义）+ v_call primitive dispatch
- [x] 1.8 docs/design/jit.md 更新 jit_to_str 签名
