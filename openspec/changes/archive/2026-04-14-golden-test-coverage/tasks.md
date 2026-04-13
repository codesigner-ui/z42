# Tasks: L1 Golden Test 覆盖补全

> 状态：🟢 已完成 | 创建：2026-04-13

**变更说明：** 补全 L1 特性中零覆盖或严重不足的 golden test；修复发现的 bug
**原因：** 审计发现 5 个高影响缺口
**文档影响：** docs/design/ir.md（ConstChar 指令）

- [x] 1.1 golden test 52_numeric_aliases — C# 数值别名（byte/sbyte/short/ushort/uint/ulong/double）
- [x] 1.2 golden test 53_expression_body — 顶层 + class 实例 + static 表达式体方法
- [x] 1.3 golden test 54_char_type — char 字面量、to_str、比较
- [x] 1.4 golden test 55_multilevel_virtual — 3 级继承、虚方法分发、is 类型测试
- [x] 1.5 dotnet test (390) + test-vm.sh (104) 全绿

## Bug fixes discovered during testing

- [x] 2.1 fix(vm): value_to_str 缺少 Value::Char 处理 → 输出数字而非字符
- [x] 2.2 feat(ir): 新增 ConstChar 指令（IR + zbc + zasm + VM interp + JIT），char 字面量不再退化为 i32
- [x] 2.3 fix(codegen): pseudo-class builtin 误拦截用户类方法（calc.Add → __list_add）
       — 新增 _classInstanceVars 跟踪 + IsReceiverClassInstance 检查
