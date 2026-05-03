# Spec: D2b — ISubscription wrapper 体系

## ADDED Requirements

### Requirement: `ISubscription<TDelegate>` interface defines wrapper protocol

#### Scenario: 接口在 stdlib 可见
- **WHEN** `using Std;` + 自定义类 `class MyWrapper : ISubscription<Action<int>> { ... }`
- **THEN** 类型检查通过；接口要求实现 `TryGet()` / `IsAlive` / `OnInvoked()`

### Requirement: `StrongRef<TDelegate>` is the trivial pass-through wrapper

#### Scenario: 强引用包装总是 alive
- **WHEN** `var s = new StrongRef<Action<int>>((int x) => {});`
- **THEN** `s.IsAlive == true`；`s.TryGet()` 返回 handler；`s.OnInvoked()` 是 nop

### Requirement: `OnceRef<TDelegate>` becomes inactive after first invoke

#### Scenario: 单次触发后失活
- **WHEN** ```z42
  var o = new OnceRef<Action<int>>((int x) => {});
  o.OnInvoked();
  ```
- **THEN** 第一次 OnInvoked 后 `o.IsAlive == false`；`o.TryGet()` 返回 null

### Requirement: `CompositeRef<TDelegate>` combines mode flags via fusion

#### Scenario: 显式构造 once 模式
- **WHEN** `var c = new CompositeRef<Action<int>>(handler, ModeFlags.Once);`
- **THEN** OnInvoked 后 `c.IsAlive == false`

#### Scenario: 链式 AsOnce 复用 CompositeRef（融合，design Decision B）
- **WHEN** ```z42
  var h = (int x) => {};
  var c1 = h.AsOnce();        // CompositeRef(handler, Once)
  var c2 = c1.AsOnce();       // 仍同一 mode；不创建新实例
  ```
- **THEN** c2 是 c1 同 mode 实例；不新增分配（v1 实现为 same instance return）

### Requirement: `MulticastAction<T>` accepts ISubscription wrappers

#### Scenario: ISubscription 重载
- **WHEN** ```z42
  var bus = new MulticastAction<int>();
  Action<int> h = (int x) => {};
  bus.Subscribe(h.AsOnce());
  bus.Invoke(1);     // 触发 h 一次
  bus.Invoke(2);     // h.OnInvoked 已 latch；不再触发
  ```
- **THEN** 第一次 invoke 调用 h；第二次跳过

### Requirement: Strong fast path keeps zero overhead (优化 A)

`Subscribe(Action<T> handler)`（D2a 路径）必须**不**经过 ISubscription 包装；直接进 strong vec，invoke 走 fast loop。advanced vec 仅承载 ISubscription 实例。

#### Scenario: 裸 handler 走 fast 通道
- **WHEN** `bus.Subscribe(h);` （未包 wrapper）
- **THEN** 内部 strong vec 长度 +1；advanced vec 不变

#### Scenario: ISubscription 走 advanced 通道
- **WHEN** `bus.Subscribe(h.AsOnce());`
- **THEN** advanced vec 长度 +1；strong vec 不变

### Requirement: Stateless wrapper skips OnInvoked (优化 C)

`StrongRef.OnInvoked()` 是 nop —— invoke loop 检测 mode 仅含无状态 flag 时直接跳过 OnInvoked 调用。CompositeRef without Once flag 同理。

#### Scenario: StrongRef 不调用 OnInvoked
- **WHEN** advanced vec 含一个 StrongRef，invoke 触发
- **THEN** 处理后不调 OnInvoked（隐含验证：StrongRef 的 OnInvoked 是 noop，调或不调对结果无影响；性能优化是不调）

## MODIFIED Requirements

### Requirement: `MulticastAction<T>` 内部双存储

**Before**（D2a）: 单一 `Action<T>[] handlers + bool[] alive`。

**After**: `Action<T>[] strong + bool[] strongAlive` + `ISubscription<Action<T>>[] advanced`。Subscribe 双重载：`Action<T>` 进 strong，`ISubscription<Action<T>>` 进 advanced。Invoke 先 strong fast loop（无 wrapper 检查），再 advanced slow loop（TryGet + OnInvoked）。

## IR Mapping

无新 IR 指令。复用 D2a + 现有 Call / VCall / CallIndirect。

## Pipeline Steps

- [x] Lexer / Parser / AST — 无影响
- [x] TypeChecker — 无影响（generic interface + class 路径已成熟）
- [x] IR Codegen / VM — 无影响
- [ ] stdlib（z42.core）— ISubscription / StrongRef / OnceRef / CompositeRef
- [ ] stdlib MulticastAction.z42 — 加重载 + advanced 路径
