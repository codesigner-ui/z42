# Proposal: delegate reference equality + MulticastAction.Unsubscribe

## Why

z42 当前类型系统中 `Action<T>` / `Func<T,R>` / `Predicate<T>` 等 delegate 类型在运行时是 `Z42FuncType` 值（VM `Value::FuncRef` / `Value::Closure` / `Value::StackClosure`），**无 IEquatable 实现**。

这阻塞两件事：
1. **D2c `obj.X -= h` desugar** —— `event MulticastAction<T>` 的 `-=` 必须找到与 h 引用相等的 handler 移除，没有 delegate equality 无法实现
2. **`MulticastAction.Unsubscribe(Action<T> handler)`** API（D-5 deferred 跟踪项）—— 只 token-based dispose 满足 95% 用例，但 `-=` 模式必须 by-handler

VM `GcRef::ptr_eq` 已在 `Object.ReferenceEquals` (`__obj_ref_eq`) 中验证 GC 引用相等机制；本 spec 把同款 ptr_eq 应用到 delegate 三个 Value 变体。

是 D2c 全设计的硬前置（用户选 A 全 D2c per design 时一并落）。

## What Changes

- **VM corelib**：`__delegate_eq(a: Object, b: Object) -> bool` builtin
  - 双 `Value::FuncRef` —— `fn_name` 字符串相等
  - 双 `Value::Closure` —— `fn_name` 相等 **且** `env` GcRef ptr_eq
  - 双 `Value::StackClosure` —— `fn_name` 相等 **且** `env_idx` 相等（同 frame 同槽）
  - 跨变体（如 FuncRef vs Closure）—— false（cardinality 不同）
  - 任一非 delegate 值 —— false（不报类型错）
  - null 处理：双 null 相等；单 null 不等
- **stdlib `Std.Delegates`**（新静态类）：暴露 `static extern bool ReferenceEquals(Object? a, Object? b)` —— 不放 `Std.Object` 因 [SymbolTable.cs:111](src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs#L111) 跳过 Object 跨 CU 导出（synthetic stub），其他 stdlib 文件无法 `Object.X` 调用 static 方法
- **stdlib `Std.MulticastAction<T>`**：加 `Unsubscribe(Action<T> handler)` 方法 —— 线性扫描 strong[] 用 `__delegate_eq` 比较，命中则置 alive=false
- **测试**：单元 + golden 覆盖三个 Value 变体 + 跨变体 + Unsubscribe 端到端

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/object.rs` | MODIFY | 新增 `builtin_delegate_eq` 函数（mirror `builtin_obj_ref_eq`） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 在 `dispatch_table()` 注册 `__delegate_eq` |
| `src/libraries/z42.core/src/Delegates.z42` | NEW | 新静态类 `Std.Delegates`，暴露 `[Native("__delegate_eq")] static extern bool ReferenceEquals(Object?, Object?)` |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | z42.core 文件数 38 → 39（+ Delegates.z42） |
| `src/libraries/z42.core/src/MulticastAction.z42` | MODIFY | 加 `Unsubscribe(Action<T>)` 方法 |
| `src/runtime/src/corelib/object_tests.rs` 或现有 `object_*_tests.rs` | MODIFY/NEW | builtin_delegate_eq 单元测试（覆盖 3 个变体 + 跨变体 + null） |
| `src/runtime/tests/golden/run/multicast_unsubscribe/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/multicast_unsubscribe/expected_output.txt` | NEW | 预期输出 |

**只读引用**：

- `src/runtime/src/metadata/types.rs:125-159` — Value enum delegate variants
- `src/runtime/src/gc/refs.rs:73` — GcRef::ptr_eq 实现
- `src/compiler/z42.IR/IrModule.cs:148` — `BuiltinInstr` 定义（不改）
- `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs:53-72` — `[Native]` lowering（不改）

## Out of Scope

- D2c event keyword（独立 spec 2）
- D2c interface event default（独立 spec 3）
- WeakRef wrapper（D-1 独立）
- Closure 内容相等比较（仅 reference equality；用户需要 deep value compare 自己做）
- `MulticastAction.Unsubscribe` 的 advanced 通道支持 —— wrapper 比较语义不同（需要 `IsAlive` / 用户定义），暂只 strong path

## Open Questions

- [ ] stdlib API 命名：`DelegateReferenceEquals(a, b)` vs 直接复用 `Object.ReferenceEquals` 增加 delegate handling？倾向独立命名，避免 Object 路径的语义复杂化
