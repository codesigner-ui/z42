# Design: WeakRef ISubscription wrapper（D-1b）

## Architecture

```
User code
  │
  │  bus.SubscribeAdvanced(new WeakRef<Action<int>>(listener.OnEvent));
  │
  ▼
Std.WeakRef<TD>  (stdlib z42 类)
  │
  │  ctor: target = DelegateOps.GetTarget(handler)
  │        if target != null: weakHandle = WeakHandle.MakeWeak(target); _isDegraded = false
  │        else:               _strong = handler;                       _isDegraded = true
  │
  │  Get():       _isDegraded ? _strong : (Upgrade(weakHandle)? handler : null)
  │  IsAlive():   _isDegraded || Upgrade(weakHandle) != null
  │  OnInvoked(): noop
  │
  ▼
Std.DelegateOps.GetTarget(d) → extern → __delegate_target
  │
  ▼
VM builtin __delegate_target  (Rust)
  │
  ├── Closure { env: [first, ...] } where first is Object → Object
  ├── Closure { env: [], ... }                            → Null
  ├── Closure { env: [non-object, ...] }                  → Null
  ├── StackClosure                                        → Null
  ├── FuncRef                                             → Null
  └── 其他                                                  → Null

WeakHandle.MakeWeak(obj) → __obj_make_weak (D-1a)
WeakHandle.Upgrade(h)    → __obj_upgrade_weak (D-1a)

MulticastAction.Invoke loop
  │
  │ snapAdv = snapshot(advanced subscriptions)
  │ for each w in snapAdv:
  │     if !w.IsAlive() continue;        // ← lapsed listener 在这里短路
  │     w.Get()(arg);
  │     w.OnInvoked();
```

## Decisions

### Decision 1：`__delegate_target` 是 builtin（VM-level），不是纯 z42 实现

**问题**：从 Closure 提取 env[0] 该走 builtin 还是有别的方式？

**选项**：
- A — 加 builtin `__delegate_target`，VM 层直接读 Closure.env
- B — 引入新 IR 指令 `DelegateGetTarget` 专属 opcode
- C — 让 z42 暴露 `Closure` 类型，用户能 `closure.Env[0]`（Reflection 风格）

**决定**：**A**

**理由**：
- A 与 D-1a `__obj_make_weak` / `__obj_upgrade_weak` 风格一致：底层暴露最小操作，stdlib 包装出语义类型
- B 成本不匹配收益（一个新 opcode 仅服务一个 stdlib 类，bench/JIT 都要更新）
- C 把 Closure 内部表示暴露给用户代码 → 序列化 / monomorphize / 跨 zpkg 都要保稳，过早 API commitment

### Decision 2：`__delegate_target` 退化策略 = 返回 null（lenient），调用方决定如何处理

**问题**：FuncRef / StackClosure / 无捕获 lambda 都没"可弱持的 target"，应抛错还是静默返回 null？

**决定**：**返回 null**（与 D-1a `__obj_make_weak` 对非 Object 返回 null 同款）

**理由**：
- 用户构造 `WeakRef<TD>(h)` 时不一定知道 h 的内部表示（lambda vs method group），如果抛错就要在用户层做大量 try/catch
- 让 stdlib `WeakRef` 收 null 后退化为 strong（带 `_isDegraded` flag）—— 行为对用户透明，**不静默丢"想要弱"的语义**而是变成"无法弱故强"
- 文档明确这一退化（per design line 191）

### Decision 3：CompositeRef.Mode.Weak 实现 = 内部嵌一个 WeakHandle，不组合 WeakRef 实例

**问题**：CompositeRef 是 D2b 已有的 multi-flag wrapper（当前支持 Once）。Weak flag 接入是"嵌一个 WeakRef 实例"还是"自己内部存 WeakHandle"？

**选项**：
- A — CompositeRef 内部 `private WeakRef<TD>? _weak;` 当 Mode.Weak 设置时构造，Get/IsAlive 委派 _weak
- B — CompositeRef 内部 `private WeakHandle? _weakHandle;` 直接存，Get/IsAlive 自己实现弱逻辑

**决定**：**B**

**理由**：
- A 多一层间接 + 重复存 handler（_weak.handler + CompositeRef.handler）
- B 共用 CompositeRef.handler 字段，只多一个 `_weakHandle` 引用，内存更小
- WeakRef 类自身保持简单，不被 CompositeRef 内部依赖反向耦合

### Decision 4：本变更不动 method group conversion 的 IR 形态

**问题**：探索发现 instance method conversion `obj.Method` 应该 emit `Closure { env: [obj], ... }`。如果实施时发现实际 emit 的是 FuncRef（无 env），weak 持 listener 的核心场景失效。

**决定**：**实施时先验证**。若发现 method group 走 FuncRef → 立即停下来报告，开新 spec 调整 method group conversion；本 spec 暂停（违反 stop-and-ask 规则的反例）

**理由**：method group conversion 改动属于 D-1 / D-1c 的 lang 行为，不能埋在 D-1b 的 stdlib spec 里偷偷做。Spec scope 边界守住

## Implementation Notes

### Rust builtin 模板

```rust
// src/runtime/src/native/...
pub fn delegate_target(args: &[Value]) -> Result<Value> {
    if args.len() != 1 { bail!("__delegate_target: expected 1 arg"); }
    match &args[0] {
        Value::Closure { env, .. } => {
            // env is GcRef<Vec<Value>>
            let env_ref = env.borrow();
            match env_ref.first() {
                Some(Value::Object(rc)) => Ok(Value::Object(Rc::clone(rc))),
                _ => Ok(Value::Null),
            }
        }
        _ => Ok(Value::Null),
    }
}
```

### z42 stdlib WeakRef

```z42
namespace Std;

public sealed class WeakRef<TD> : ISubscription<TD> {
    private TD _handler;
    private WeakHandle? _weakHandle;
    private bool _isDegraded;

    public WeakRef(TD handler) {
        this._handler = handler;
        var target = DelegateOps.GetTarget(handler);
        if (target != null) {
            this._weakHandle = WeakHandle.MakeWeak(target);
            this._isDegraded = false;
        } else {
            this._weakHandle = null;
            this._isDegraded = true;
        }
    }

    public TD? Get() {
        if (this._isDegraded) return this._handler;
        var live = WeakHandle.Upgrade(this._weakHandle);
        return live != null ? this._handler : null;
    }

    public bool IsAlive() {
        if (this._isDegraded) return true;
        return WeakHandle.Upgrade(this._weakHandle) != null;
    }

    public void OnInvoked() { /* noop */ }
}
```

### CompositeRef Weak 接入

修改 `CompositeRef` 现有实现：构造时若 `Modes & Weak != 0`，调 `DelegateOps.GetTarget(handler)` 拿 target，存 `_weakHandle`；Get / IsAlive 路径多一个 weak 检查分支。Once 与 Weak 行为正交，可叠加。

## Testing Strategy

- **单元测试**：
  - Rust：`__delegate_target` 单测覆盖 Closure with env / Closure no env / StackClosure / FuncRef / null
  - z42：`WeakRef.Get/IsAlive` 在 strong-degraded 与 weak 路径下行为
- **golden test**：
  - `weak_subscription_lapsed`：subscribe weak handler → 释放 listener → 触发 GC → fire 不调用
  - `composite_ref_weak_mode`：CompositeRef(handler, Once|Weak) 在 lapsed + once-consume 复合下行为
- **VM 验证**：dotnet test + ./scripts/test-vm.sh
