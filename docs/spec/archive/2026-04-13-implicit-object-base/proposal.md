# Proposal: 隐式 Object 继承

## Why

当前所有 class 如果没有显式写 `: Base`，其 `BaseClass` 为 null，不继承任何类。这导致：
- `override ToString()` 的 TypeChecker 验证被跳过（无基类 → 无虚方法表 → 不报错也不校验）
- vtable 中没有 Object 的虚方法（ToString/Equals/GetHashCode），VM 靠 fallback builtin 兜底
- 与 C# 语义不一致：C# 中所有 class 隐式继承 `System.Object`

## What Changes

- 所有 class（非 struct/record/Object 本身）隐式继承 `Std.Object`
- TypeChecker 注册 Object 的虚方法，使 `override ToString()` 能正确验证
- IrGen 为无基类 class 输出 `base_class: "Std.Object"`
- VM loader 为继承 Std.Object 的类合成 Object 虚方法到 vtable（指向 builtin stub）

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.Semantics/TypeCheck/TypeChecker.cs` | 修改 | 注册 Object 虚方法 + 隐式基类 |
| `z42.Semantics/Codegen/IrGen.cs` | 修改 | EmitClassDesc 输出隐式 base |
| `src/runtime/src/metadata/loader.rs` | 修改 | build_type_registry 合成 Object vtable |
| `src/runtime/src/corelib/object.rs` | 新增 | Object builtin 实现（__obj_equals, __obj_hash_code） |
| `src/runtime/src/corelib/mod.rs` | 修改 | 注册新 builtin |
| Golden tests | 新增 | 验证隐式继承 + override 校验 |

## Out of Scope

- struct/record 不继承 Object（编译器合成值语义方法，不走 vtable）
- GetType() 实现（已有 extern，不在本次范围）
- 接口的 Object 方法继承

## Open Questions

- [x] Object 本身如何排除自引用 → 按类名 `Object` 或 `Std.Object` 判断
