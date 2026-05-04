# Spec: WeakRef ISubscription wrapper

## ADDED Requirements

### Requirement: `__delegate_target` builtin 提取 delegate captured target

提供 corelib builtin `__delegate_target(d: Value::Closure | Value::StackClosure | Value::FuncRef) -> Value::Object | Value::Null`，
返回 delegate 内部捕获的 target 对象，按以下规则：

- `Value::Closure { env: [obj, ...] }`（env 第一项是 instance method 的 receiver 对象）→ 返回 `env[0]` 解包的 Object
- `Value::Closure { env: [] }`（无捕获 lambda）→ 返回 `Null`
- `Value::Closure { env: [non-object, ...] }`（捕获仅基础类型）→ 返回 `Null`（基础类型无法 weak hold）
- `Value::StackClosure` → 返回 `Null`（stack 上不能 weak hold）
- `Value::FuncRef` → 返回 `Null`（free function 无 receiver）
- 其他 Value 变体 → 返回 `Null`（lenient，与 `__obj_make_weak` 一致）

#### Scenario: instance method delegate 提取 receiver

- **WHEN** `class Foo { void Bar() {} }` 实例化 `var f = new Foo();`，`Action h = f.Bar;`
- **THEN** `__delegate_target(h)` 返回 `f` 这个 Object（同一对象身份）

#### Scenario: 无捕获 lambda 返回 null

- **WHEN** `Action h = () => Console.WriteLine("hi");`（无外部捕获）
- **THEN** `__delegate_target(h)` 返回 `Null`

#### Scenario: stack closure 返回 null

- **WHEN** delegate 是 stack-allocated（escape analysis 判定无逃逸）
- **THEN** `__delegate_target` 返回 `Null`，调用方应识别并退化为 strong hold

### Requirement: `Std.WeakRef<TD> : ISubscription<TD>` stdlib 类

新增 stdlib 类 `Std.WeakRef<TD>`，实现 `ISubscription<TD>`：

- 构造 `WeakRef(TD handler)`：调 `__delegate_target(handler)` → target；若 target 非 null → `WeakHandle.MakeWeak(target)`；若 null → 退化 strong（直接持 handler）+ 设 `_isDegraded = true` 标志
- `Get() -> TD?`：若退化 strong → 返回 strong handler；若 weak → `WeakHandle.Upgrade(weakHandle)` 检查 → 已 GC 返回 null
- `IsAlive() -> bool`：weak 路径下查 target 是否还活；strong 路径恒 true
- `OnInvoked()`：noop（weak 不维护本地状态）

#### Scenario: 弱持 listener 在 GC 后失效

- **WHEN** `class Listener { ... }` 实例 + handler 弱持订阅 `bus.SubscribeAdvanced(new WeakRef<Action<int>>(listener.OnEvent))` → `listener = null` → `gc::collect()` → `bus.Invoke(1)`
- **THEN** invoke loop 检查到 `IsAlive()=false`，跳过本次 callback；listener 实际被回收

#### Scenario: free lambda 退化 strong

- **WHEN** `bus.SubscribeAdvanced(new WeakRef<Action<int>>((x) => Console.WriteLine(x)))`
- **THEN** 构造时 `__delegate_target` 返回 null，WeakRef 退化 strong；后续行为等价 `StrongRef`

### Requirement: CompositeRef.Mode.Weak flag 接入

`Std.CompositeRef<TD>` 当 `Modes & Weak != 0` 时，`Get` / `IsAlive` / `OnInvoked` 路径行为与内嵌一个 `WeakRef<TD>` 等价。

#### Scenario: `Mode.Weak | Mode.Once` 复合行为

- **WHEN** `bus.SubscribeAdvanced(new CompositeRef<Action<int>>(handler, Mode.Weak | Mode.Once))` → handler 持 listener → fire 一次（成功）→ listener GC → fire 第二次
- **THEN** 第一次 fire callback 调用 + Once 把 wrapper 标 consumed；第二次 fire 因 IsAlive=false 跳过；本来 Once 也会因 consumed 跳过，两个 flag 共存无冲突

## IR Mapping

无新 IR 指令。`__delegate_target` 作为 `BuiltinInstr` 走现有 native call 路径。

## Pipeline Steps

- [x] Lexer（无变化）
- [x] Parser / AST（无变化）
- [x] TypeChecker（仅 stdlib 加 extern declaration `Std.DelegateOps.GetTarget`）
- [x] IR Codegen（extern method 走现有路径，emit BuiltinInstr）
- [x] VM interp（`__delegate_target` 实现 + 注册到 builtin registry；JIT 复用 interp 路径或加 helper）
