# Design: delegate reference equality

## Architecture

```
[Native("__delegate_eq")]                 corelib/object.rs::builtin_delegate_eq
static extern bool                  ─────► match (a, b) {
DelegateReferenceEquals(a, b)              (FuncRef(s1),    FuncRef(s2))    => s1 == s2
                                           (Closure{...},   Closure{...})   => fn_eq && env.ptr_eq
                                           (StackClos{...}, StackClos{...}) => fn_eq && env_idx_eq
                                           (Null, Null)                    => true
                                           _                               => false
                                         }

stdlib MulticastAction<T>.Unsubscribe(h)   linear scan strong[]
                                            for i in 0..strongCount:
                                              if strongAlive[i]
                                                 && DelegateReferenceEquals(strong[i], h):
                                                strongAlive[i] = false
```

## Decisions

### Decision 1: ptr_eq vs 结构相等 for closures
**问题：** 同 lambda 在不同 outer call 中创建的两个 `Closure` 实例，captured value 完全相同，是否应相等？
**选项：**
- A. **ptr_eq only**（reference equality）：env GcRef alloc 不同就不等，即使 captured 内容相同
- B. **结构相等**：fn_name + env values element-wise compare

**决定：A（ptr_eq）**。原因：
- "delegate equality" 本质是 reference identity（C# 同款）；语义等同于 `Object.ReferenceEquals`
- B 需要递归 compare captured values（含其他 delegate / Object 引用 → 又需要递归），语义复杂且性能高
- 用户场景：`bus.Subscribe(h); bus.Unsubscribe(h)` —— 同一变量 `h` 两端就是 ptr_eq；ptr_eq 命中即足够
- C# / Java 的 delegate `==` 也是 reference equality 路径

### Decision 2: stdlib API 命名 — `DelegateReferenceEquals` vs `Object.ReferenceEquals` 扩展
**问题：** 在 `Object.z42` 暴露给 stdlib 用户的方法叫什么？
**选项：**
- A. `Object.DelegateReferenceEquals(a, b)` 独立方法
- B. 让 `Object.ReferenceEquals(a, b)` 也接受 delegate（修改 `__obj_ref_eq` 内部加 delegate 处理）

**决定：A**。原因：
- 语义清晰 —— `ReferenceEquals` 用户预期 GC heap object；delegate 是另一类
- B 让 `__obj_ref_eq` 承担多种语义，回头维护成本高
- API 对称：未来加 `WeakRef` 后再加 `WeakReferenceEquals` 同款风格
- z42 stdlib 现有 `Object.ReferenceEquals` 不动（向后兼容 + scope 最小）

### Decision 3: Unsubscribe 仅扫 strong 通道（advanced 不动）
**问题：** advanced 通道存的是 `ISubscription<Action<T>>` wrapper，能否 by-handler 取消？
**决定：** v1 不支持。原因：
- advanced 通道用户传入的是 wrapper（OnceRef / CompositeRef 等），handler 包在 wrapper 内 `Get()` 返回；要 unsubscribe wrapper 需要比较 wrapper 引用，**不是** handler 引用
- 对应用例：用户用 wrapper 应该用 token-based dispose（subscribe 返回 IDisposable）
- 加复杂度（要识别"unsubscribe by inner handler" vs "unsubscribe by wrapper ref"）不值
- 文档 + 运行时不报错（线性扫不到 strong 中匹配 → no-op，advanced 完全跳过）

### Decision 4: 同 handler 多次 Subscribe 行为
**问题：** `Subscribe(h); Subscribe(h); Unsubscribe(h)` 应移除几次？
**决定：** **全部移除**（linear scan 不 break）。原因：
- 用户预期"unsubscribe h" = "h 不再被通知"
- 如果只移除一次，剩下的 h 还会被调，与"unsubscribed"语义不符
- 与 C# `MulticastDelegate` 的"-= 移除最后一次添加"行为不同 —— 但 z42 多播是显式 `MulticastAction` 而非 C# delegate combine 链，更符合 list 语义

### Decision 5: 跨 Closure / StackClosure 不等
**问题：** 同一 fn 一次走 heap、一次走 stack（escape analysis 决策不同），相等吗？
**决定：** false。原因：
- 不同 Value variant 反映了运行时不同的存储；结构上是不同对象
- 用户在源码层面可能视为"同一 lambda"，但 VM 视角两个独立 closure 实例
- 简化实现 —— variant match 完全分离

## Implementation Notes

- `corelib/object.rs::builtin_delegate_eq`：~25 行，3 个 match arm + null + fallthrough false
- `corelib/mod.rs` `dispatch_table()`：1 行 `m.insert("__delegate_eq", object::builtin_delegate_eq);`
- `Object.z42`：2 行 extern 声明
- `MulticastAction.Unsubscribe`：~12 行 while 循环 + 调用 `Object.DelegateReferenceEquals` 比较
- 编译器侧 **零改动**：`[Native]` lowering 已就绪

## Testing Strategy

- **VM 单元测试** `corelib/object_tests.rs`（或 sibling 测试文件）：9 scenarios per spec
  - FuncRef same/diff
  - Closure same/diff env
  - StackClosure same/diff env_idx
  - 跨变体（FuncRef vs Closure）
  - 跨 Closure / StackClosure
  - null + 非 delegate
- **Golden test** `multicast_unsubscribe/source.z42`：6 scenarios
  - 基本 subscribe + unsubscribe + invoke 验证
  - 未订阅 unsubscribe（no-op）
  - 重复 unsubscribe（idempotent）
  - 多 handler 精确移除
  - 同 handler 多次 subscribe → 一次 unsubscribe 全清
  - Unsubscribe 不影响 advanced 通道
- dotnet test 全绿（基线 +0，编译器零改动）
- `./scripts/test-vm.sh` +9 单元 + 1×2 golden modes = 11 tests
- `./scripts/build-stdlib.sh` 6/6 绿
