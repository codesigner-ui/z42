# Proposal: WeakRef ISubscription wrapper（D-1b）

## Why

D-1a（`expose-weak-ref-builtin`，2026-05-04）落地了 corelib `Std.WeakHandle` + `__obj_make_weak` / `__obj_upgrade_weak` 两个 builtin，给"运行时弱引用"开了底层口子。但 D-1（`docs/design/delegates-events.md` §5.2 + §5.3）真正想解决的"长寿事件源持回调导致 listener 永不 GC（lapsed listener leak）"问题需要 **stdlib `Std.WeakRef<TD> : ISubscription<TD>` wrapper 类**包住 `WeakHandle` 接入 `MulticastAction.SubscribeAdvanced`。

不做的现实痛点：
- GUI / 长寿对象持回调场景：listener 对象本可被 GC，但因被 multicast event 强引用 → 永远活着 → 等价内存泄漏
- D2b 的 `CompositeRef.Mode.Weak` 当前是 placeholder noop（用户传 `Mode.Weak` 等价 Strong），既然 flag 已暴露不做语义对齐属于半成品

## What Changes

1. 新 corelib builtin **`__delegate_target`**：从 Closure 提取 captured this/env，返回弱可持的 Object（FuncRef / StackClosure / 无 env 的 lambda 退化返回 null，调用方需识别）
2. stdlib `Std.WeakRef<TD> : ISubscription<TD>` 类：构造接受 delegate，内部用 `__delegate_target` + `WeakHandle.MakeWeak` 弱持 target；`Get()` 返回 `null`/upgraded delegate；`IsAlive()` 通过 `WeakHandle.Upgrade` 检查 target 是否还活
3. `MulticastAction.Invoke` loop 已经检查 `ISubscription.IsAlive()` —— WeakRef 的 IsAlive 自然形成 lapsed-listener 短路（无需改 invoke 路径）
4. **CompositeRef.Mode.Weak 接入**：CompositeRef 的 OnInvoked / Get 路径增加 Weak flag 处理，等价"内部嵌一个 WeakRef"
5. golden test：listener 对象主动 GC 后，weak subscription 不再触发

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/native/builtins.rs`（或 builtin 注册集） | MODIFY | 新增 `__delegate_target(Value::Closure) -> Value::Object` builtin |
| `src/runtime/src/native/registry.rs`（或同一文件） | MODIFY | 注册 `__delegate_target` 名 → 实现指针 |
| `src/libraries/z42.core/src/Delegates.z42` | MODIFY | extern `__delegate_target` 暴露为 `Std.DelegateOps.GetTarget(object delegate) -> object?` |
| `src/libraries/z42.core/src/SubscriptionRefs.z42` | MODIFY | NEW `WeakRef<TD> : ISubscription<TD>` 类（构造调 GetTarget + MakeWeak；Get/IsAlive/OnInvoked 实现） |
| `src/libraries/z42.core/src/SubscriptionRefs.z42` | MODIFY | `CompositeRef.Get` / `CompositeRef.IsAlive` / `CompositeRef.OnInvoked` 接入 Weak flag 行为（取代当前 noop） |
| `src/libraries/z42.core/src/MulticastAction.z42` | 只读 | 验证 SubscribeAdvanced + Invoke loop 与新 WeakRef 兼容（应已就绪，复核） |
| `src/libraries/z42.core/tests/golden/weak_subscription_lapsed/source.z42` | NEW | golden：subscribe weak handler → 释放 listener 引用 → 触发 GC → fire 不调用 |
| `src/libraries/z42.core/tests/golden/weak_subscription_lapsed/expected_output.txt` | NEW | golden 期望输出 |
| `src/libraries/z42.core/tests/golden/composite_ref_weak_mode/source.z42` | NEW | golden：CompositeRef(handler, Once \| Weak) 行为符合两 flag 叠加 |
| `src/libraries/z42.core/tests/golden/composite_ref_weak_mode/expected_output.txt` | NEW | golden 期望输出 |
| `docs/design/delegates-events.md` | MODIFY | §5.2 / §5.3 / status 行更新 D-1b 落地；line 191 weak target 退化策略落地说明 |
| `docs/design/vm-architecture.md`（或 builtin 文档） | MODIFY | `__delegate_target` builtin 加入清单 + 语义说明（含退化规则） |
| `docs/deferred.md` | MODIFY | 移除 D-1b 条目 |

**只读引用**：
- `spec/archive/2026-05-04-expose-weak-ref-builtin/` D-1a 落地详情（WeakHandle API / builtin 风格）
- `src/runtime/src/metadata/types.rs` `Value::Closure { env, fn_name }` / `Value::StackClosure` 表示
- `src/libraries/z42.core/src/WeakHandle.z42` D-1a stdlib 包装
- `src/libraries/z42.core/src/ISubscription.z42` 接口定义

## Out of Scope

- `__delegate_target` 不暴露给最终用户（只通过 `Std.DelegateOps.GetTarget` 在 stdlib 内部使用，避免用户手撕 closure）
- 多播 event field 的 weak 默认：D-1 设计中 multicast event 默认是强引用，user 主动用 `Sub.Subscribe(new WeakRef<Action<T>>(h))` 切换；不改默认
- `Std.WeakRef` 之外的 timing/dispatch wrapper（throttle / debounce / dispatcher）：D2b followup 范围
- `CompositeRef.Mode.Weak` 与 Mode.Once 之外的新 mode（如 Mode.Async / Mode.MainThread）

## Open Questions

- [x] **`__delegate_target` 对 instance method 形式 delegate 的语义**：z42 当前 method group conversion 把 `obj.Method` 转成 Closure（env=[obj]）还是 FuncRef？需在实施前验证 —— 设计要求 instance method 必须能 weak 持 obj，否则"weak listener 持 GUI 对象"场景失效。若发现是 FuncRef 路径要先调整 method group conversion，**这是 stop-and-ask 信号**
- [x] **WeakRef 在 invoke 时如果 target 已被 GC，应静默跳过还是报错？** → 静默跳过（Action 半部分已经 fail-fast 异常路径处理了真正的 throw），lapsed 是预期场景不抛
