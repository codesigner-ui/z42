# Spec: delegate reference equality + MulticastAction.Unsubscribe

## ADDED Requirements

### Requirement: `__delegate_eq` builtin 三个 delegate 变体的 reference equality

#### Scenario: 同 FuncRef
- **WHEN** 两个 `Value::FuncRef("Demo.Helper")` 比较
- **THEN** 返回 true

#### Scenario: 不同 FuncRef
- **WHEN** `Value::FuncRef("Demo.A")` 与 `Value::FuncRef("Demo.B")`
- **THEN** 返回 false

#### Scenario: 同 Closure（同 fn_name + 同 env GC ptr）
- **WHEN** 同一闭包对象两次取 reference 比较（env GcRef::ptr_eq → true）
- **THEN** 返回 true

#### Scenario: 异 Closure 同 fn 不同 env
- **WHEN** 同一 lambda 在两次外层调用中创建（fn_name 同；env 是两个独立 GcRef alloc）
- **THEN** 返回 false

#### Scenario: 同 StackClosure（同 fn_name + 同 env_idx）
- **WHEN** 同一 stack-allocated closure 两次 reference 比较
- **THEN** 返回 true

#### Scenario: 跨变体不等
- **WHEN** `FuncRef("F")` vs `Closure { fn_name: "F", env: ... }`（同名但 cardinality 不同）
- **THEN** 返回 false

#### Scenario: 跨 Closure 与 StackClosure 不等
- **WHEN** 同 fn_name 一个走 heap (Closure)、一个走 stack (StackClosure)
- **THEN** 返回 false（运行时 variant 不同）

#### Scenario: 非 delegate 值不报错
- **WHEN** `Value::Int(5)` vs `Value::Str("foo")` 或 `Value::Object(...)` 任意组合
- **THEN** 返回 false（不抛类型错）

#### Scenario: null 处理
- **WHEN** 双 null
- **THEN** true
- **WHEN** 一端 null 一端 delegate
- **THEN** false

### Requirement: `MulticastAction<T>.Unsubscribe(Action<T> handler)` 通过 reference equality 移除

#### Scenario: 已订阅 handler 取消后 invoke 不触发
- **WHEN** `bus.Subscribe(h); bus.Unsubscribe(h); bus.Invoke(...)`
- **THEN** h 不被调用

#### Scenario: 未订阅 handler 取消是 no-op（不抛错）
- **WHEN** 从未 Subscribe 过的 handler `bus.Unsubscribe(h)`
- **THEN** 静默返回，不报错

#### Scenario: 重复 Unsubscribe 幂等
- **WHEN** `Subscribe(h); Unsubscribe(h); Unsubscribe(h)`
- **THEN** 第二次 Unsubscribe no-op

#### Scenario: 多 handler 中精确移除一个
- **WHEN** `Subscribe(a); Subscribe(b); Subscribe(c); Unsubscribe(b); Invoke(...)`
- **THEN** 只 a 与 c 被调用

#### Scenario: 同 lambda 多次 Subscribe
- **WHEN** 同一 lambda 变量 h 两次 `Subscribe(h)`，然后 `Unsubscribe(h)`
- **THEN** 两次订阅都被移除（实现：linear scan 命中所有 ptr_eq 槽）

#### Scenario: Unsubscribe 仅作用 strong 通道
- **WHEN** `SubscribeAdvanced(wrapper)` 后 `Unsubscribe(wrapper.Get())`
- **THEN** advanced 通道不动（wrapper 通过 token 路径管理；本 v1 不深入 advanced equality）

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker（不动 —— 现有 `[Native]` lowering 路径处理）
- [ ] IR Codegen（不动）
- [x] VM interp（核心修改 —— corelib 加 `__delegate_eq`）
