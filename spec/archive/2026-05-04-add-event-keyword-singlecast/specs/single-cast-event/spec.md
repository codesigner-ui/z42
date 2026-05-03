# Spec: 单播 event 关键字

## ADDED Requirements

### Requirement: `event Action<T>` 单播 event

#### Scenario: 字段为 nullable，初值 null
- **WHEN** 类内 `public event Action<int> OnKey;` + `var c = new C();`
- **THEN** `c.OnKey` 字段类型 `Action<int>?`，初值 null

#### Scenario: 首次 += 设置成功
- **WHEN** `c.OnKey += handler;` 在字段为 null 时
- **THEN** 不抛；字段被 set 为 handler

#### Scenario: 双绑定抛 InvalidOperationException
- **WHEN** 字段已 set，再 `c.OnKey += handler2;`
- **THEN** 抛 `Std.InvalidOperationException("single-cast event already bound")`

#### Scenario: -= 引用相等时清空
- **WHEN** 字段 set 为 handler；调 `c.OnKey -= handler;`
- **THEN** 字段被清空为 null

#### Scenario: -= 引用不等是 no-op
- **WHEN** 字段 set 为 handlerA；调 `c.OnKey -= handlerB;`
- **THEN** 字段保持 handlerA，不报错

### Requirement: `event Func<T,R>` 单播

#### Scenario: 同 Action 路径
- **WHEN** `event Func<int, bool> Validate;`
- **THEN** 同上但 handler 类型为 `Func<int, bool>`

### Requirement: `event Predicate<T>` 单播

#### Scenario: 同 Action 路径
- **WHEN** `event Predicate<int> Filter;`
- **THEN** 同上但 handler 类型为 `Predicate<int>`

### Requirement: interface 单播 event 同款支持

#### Scenario: interface 内单播 event 合成
- **WHEN** interface 内 `event Action<int> OnKey;`
- **THEN** 合成 instance abstract `add_OnKey(Action<int>)` + `remove_OnKey(Action<int>)` 两个 MethodSignature（与多播 interface event 同款）

## Pipeline Steps

- [ ] Lexer
- [x] Parser / AST（SynthesizeClassEvent / SynthesizeInterfaceEvent 单播路径）
- [x] TypeChecker（既有 `+=` / `-=` desugar 路径复用）
- [ ] IR Codegen
- [ ] VM interp
