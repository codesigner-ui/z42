# Spec: Z42Type record 结构 equality

## ADDED Requirements

### Requirement: Z42InstantiatedType 同定义 + 同 type-args 视为相等

#### Scenario: 单层相等
- **WHEN** 两次构造 `Foo<int>`（同一 `Foo` 定义、不同 list 对象）
- **THEN** `a.Equals(b) == true` 且 `a.GetHashCode() == b.GetHashCode()`

#### Scenario: 嵌套相等
- **WHEN** 两次构造 `Foo<Bar<int>>`
- **THEN** Equals true、Hash 一致

#### Scenario: 不同定义 / 不同 args / 不同 arity 不等
- **WHEN** `Foo<int>` vs `Bar<int>` / `Foo<int>` vs `Foo<string>` / `Foo<int>` vs `Foo<int, string>`
- **THEN** Equals false

### Requirement: Z42InterfaceType 同名 + 同 TypeArgs 视为相等

#### Scenario: 实例化接口相等
- **WHEN** 两次构造 `ISubscription<Action<int>>` 表示
  （Name="ISubscription"、Methods 引用相同、TypeArgs 元素相同但 list 对象不同）
- **THEN** Equals true、Hash 一致
- **AND** `IsAssignableTo` 间接也 true

#### Scenario: 非泛型接口
- **WHEN** 两次构造 `IDisposable`（TypeArgs=null）
- **THEN** Equals true（TypeArgs 都 null 视为相等）

#### Scenario: 不同实例化
- **WHEN** `ISubscription<int>` vs `ISubscription<string>`
- **THEN** Equals false

### Requirement: Z42FuncType 同 Params + 同 Ret 视为相等

#### Scenario: 函数类型相等
- **WHEN** 两次构造 `(int, string) -> bool`（Params/Ret 元素相同但 list 不同）
- **THEN** Equals true、Hash 一致

#### Scenario: arity / 参数 / 返回类型不同
- **WHEN** `(int) -> bool` vs `(int, int) -> bool` / `(int) -> bool` vs `(string) -> bool` / `(int) -> bool` vs `(int) -> int`
- **THEN** Equals false

### Requirement: GetHashCode 与 Equals 一致

#### Scenario: HashSet 查找命中
- **WHEN** `set.Add(Foo<int>); set.Contains(Foo<int>)`（不同对象、结构相等）
- **THEN** Contains true（对所有三个 record 都成立）

### Requirement: 触发 D2b 真实场景

#### Scenario: ISubscription wrapper 字段赋值
- **WHEN** stdlib 中 `ISubscription<Action<T>>` 字段赋值同类型 wrapper（`this.advanced[i] = w`）
- **THEN** TypeChecker 不再报 "cannot assign ISubscription<(T)->void> to ISubscription<(T)->void>"
- **NOTE** D2b 完整解封还需 Spec 3（member substitution），本 spec 只确保赋值不再因 type equality 失败

## MODIFIED Requirements

### Requirement: 类型 assignability 在三个 record 上使用结构相等

**Before:** `Z42InstantiatedType` / `Z42InterfaceType` / `Z42FuncType` 通过 record 默认 Equals
比较，因 `IReadOnlyList<T>` 引用比较失败；现实代码靠 IsAssignableTo 中的 ad-hoc workaround 兜底（仅 Instantiated 与 FuncType 有，InterfaceType 没有）。

**After:** Equals override 内部走 element-wise compare；assignability 自然修正；workaround 分支保留作防御性。

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [x] TypeChecker（核心修改 — Z42Type 内部）
- [ ] IR Codegen
- [ ] VM interp
