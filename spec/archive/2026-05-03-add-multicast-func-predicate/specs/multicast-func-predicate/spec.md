# Spec: MulticastFunc + MulticastPredicate stdlib

## ADDED Requirements

### Requirement: `Std.MulticastFunc<T, R>` 多播 Func

#### Scenario: Subscribe + Invoke 收集结果数组
- **WHEN** `var bus = new MulticastFunc<int, int>(); bus.Subscribe((int x) => x + 1); bus.Subscribe((int x) => x * 2); var r = bus.Invoke(5);`
- **THEN** `r = [6, 10]`（FIFO 顺序）

#### Scenario: 空 bus Invoke 返回空数组
- **WHEN** `var r = bus.Invoke(0)` 在 0 个订阅上
- **THEN** `r.Length == 0`

#### Scenario: Subscribe 返回 IDisposable token
- **WHEN** `var t = bus.Subscribe(handler);`
- **THEN** `t` 是 IDisposable，`t.Dispose()` 取消该订阅

#### Scenario: Unsubscribe by handler
- **WHEN** `bus.Subscribe(h); bus.Unsubscribe(h);` 然后 Invoke
- **THEN** h 不被调用，`r.Length` 减 1

#### Scenario: SubscribeAdvanced wrapper 路径
- **WHEN** `bus.SubscribeAdvanced(new OnceRef<Func<int,int>>(handler))`
- **THEN** wrapper 走 advanced 通道，OnInvoked 后 IsAlive=false（同 MulticastAction）

#### Scenario: continueOnException=false（默认）首抛即停
- **WHEN** 多个 handler，第二个抛异常
- **THEN** Invoke 抛该异常，前面值丢失（D2d-2 加聚合）

### Requirement: `Std.MulticastPredicate<T>` 多播 Predicate

#### Scenario: Invoke 返回 bool 数组
- **WHEN** `var v = new MulticastPredicate<int>(); v.Subscribe((int x) => x > 0); v.Subscribe((int x) => x < 100); var r = v.Invoke(5);`
- **THEN** `r = [true, true]`

#### Scenario: All 短路 false
- **WHEN** 三个 handler [`x > 0`, `x < 0`（false 时短路）, `x == 5`]，调 `v.All(5)`
- **THEN** 返回 false；第三个 handler 不被调用

#### Scenario: Any 短路 true
- **WHEN** 三个 handler [`x < 0`（false）, `x == 5`（true）, `x > 100`]，调 `v.Any(5)`
- **THEN** 返回 true；第三个不被调用

#### Scenario: 空 bus All / Any
- **WHEN** 0 订阅
- **THEN** `All(...)` 返回 true（空集合 universal quant 真），`Any(...)` 返回 false

### Requirement: event keyword 类型校验放宽

#### Scenario: 多播 event with MulticastFunc 合成 add/remove
- **WHEN** 类内 `public event MulticastFunc<int, bool> Validate;`
- **THEN** parser 合成 `add_Validate(Func<int, bool>): IDisposable` + `remove_Validate(Func<int, bool>)` 方法

#### Scenario: 多播 event with MulticastPredicate 合成 add/remove
- **WHEN** `public event MulticastPredicate<int> Filter;`
- **THEN** parser 合成 add_Filter(Predicate<int>) / remove_Filter(Predicate<int>)

#### Scenario: interface 多播 event with MulticastFunc/Predicate
- **WHEN** interface 内 `event MulticastFunc<int, bool> Validate;`
- **THEN** 合成 instance abstract MethodSignatures（add_Validate / remove_Validate）

## Pipeline Steps

- [ ] Lexer
- [x] Parser / AST（SynthesizeClassEvent / SynthesizeInterfaceEvent 放宽）
- [ ] TypeChecker（不动 —— 走既有方法 dispatch 路径）
- [ ] IR Codegen
- [x] VM interp（不动 —— stdlib 类纯 z42 实现，复用现有 generic 路径）
