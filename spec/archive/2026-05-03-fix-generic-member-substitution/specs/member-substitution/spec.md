# Spec: generic 实例链式 member 访问 substitution propagation

> **🟢 INVESTIGATED — 所有 scenarios 在 Spec 1+2 落地后自动通过**，
> 由 `src/compiler/z42.Tests/GenericMemberAccessTests.cs` 7 个测试覆盖。
> 见 proposal.md 探索结论；本文件保留 scenarios 作为回归测试规约。

## ADDED Requirements

### Requirement: 数组 element 是 InstantiatedType，元素 member 可访问

#### Scenario: `arr[i].field` 直接字段
- **WHEN** `Foo<int>[] arr;` 且 `class Foo<T> { public T value; }`，访问 `arr[i].value`
- **THEN** value 类型解为 `int`（substitution 已应用）

#### Scenario: `arr[i].field.method()` 链式
- **WHEN** `class Slot<T> { public ISubscription<T> sub; }` `Slot<int>[] arr;`，访问 `arr[i].sub.IsAlive()`
- **THEN** sub 类型解为 `ISubscription<int>`，IsAlive 解析成功

#### Scenario: 嵌套 generic 元素
- **WHEN** `Foo<Bar<T>>[] arr;`，访问 `arr[i].field`
- **THEN** 两级 substitution 都应用

### Requirement: 直接 instantiated 实例访问保持原行为

#### Scenario: 简单 `inst.field` 不退化
- **WHEN** `Foo<int> x;` 访问 `x.value`
- **THEN** 类型仍为 `int`（regression baseline）

#### Scenario: 方法调用 substitution
- **WHEN** `class Foo<T> { public T get() { ... } }`，`Foo<int> x; x.get()`
- **THEN** 返回类型 `int`（regression baseline）

### Requirement: D2b 真实场景

#### Scenario: ISubscription wrapper 通过 generic 容器
- **WHEN** stdlib 中 `class _Slot<T> { public ISubscription<T> sub; bool alive; }` `_Slot<Action<T>>[] slots; slots[i].sub.OnInvoked()`
- **THEN** 编译通过；OnInvoked 解析正确

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [x] TypeChecker（核心修改）
- [ ] IR Codegen
- [ ] VM interp
