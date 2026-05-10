# Spec: 引用/值类型约束（Reference / Value Type Constraints）

## ADDED Requirements

### Requirement: `class` 约束语法

`where T: class` — T 必须是引用类型（class, interface, array, T?, string）。

#### Scenario: class 约束通过
- **WHEN** `class Foo { }` 和 `void F<T>(T x) where T: class { } void Main() { F(new Foo()); }`
- **THEN** 类型检查通过

#### Scenario: class 约束 + 接口组合
- **WHEN** `void F<T>(T x) where T: class + IDisplay { x.Show(); }` 配合适当类
- **THEN** 类型检查通过

#### Scenario: 值类型违反 class 约束
- **WHEN** `void F<T>(T x) where T: class { } void Main() { F(42); }`
- **THEN** 报错 `E0402`：`type argument 'int' for 'T' does not satisfy constraint 'class'`

### Requirement: `struct` 约束语法

`where T: struct` — T 必须是值类型（primitive int/bool/float/double/char 或 `isStruct=true` 的 class）。

#### Scenario: struct 约束通过（primitive）
- **WHEN** `void F<T>(T x) where T: struct { } void Main() { F(42); }`
- **THEN** 类型检查通过

#### Scenario: struct 约束通过（用户 struct）
- **WHEN** `struct Point { int x; int y; }` 和 `void F<T>(T x) where T: struct { } void Main() { F(new Point()); }`
- **THEN** 类型检查通过

#### Scenario: 引用类型违反 struct 约束
- **WHEN** `class Foo { } void F<T>(T x) where T: struct { } void Main() { F(new Foo()); }`
- **THEN** 报错 `E0402`：`type argument 'Foo' for 'T' does not satisfy constraint 'struct'`

### Requirement: 互斥校验

同一类型参数不能同时带 `class` 和 `struct` flag。

#### Scenario: 互斥报错
- **WHEN** `void F<T>(T x) where T: class + struct { }`
- **THEN** 报错 `E0402`：`generic parameter 'T' cannot be both 'class' and 'struct'`

### Requirement: 与其他约束组合

`class` / `struct` 可与基类、接口约束共存（允许冗余，校验通过即可）。

#### Scenario: class + interface 组合
- **WHEN** `void F<T>(T x) where T: class + IDisplay { x.Show(); }`
- **THEN** 类型检查通过

#### Scenario: struct + interface 组合
- **WHEN** `void F<T>(T x) where T: struct + IEquatable<T> { x.Equals(x); }`
- **THEN** 类型检查通过（假设有 primitive 实现 IEquatable，否则在调用点校验失败）

#### Scenario: 基类与 class 混用
- **WHEN** `class Animal { } void F<T>(T x) where T: Animal + class { }`
- **THEN** 类型检查通过（class 冗余但允许）

## IR Mapping

- flag 约束**不**写入 zbc；仅编译期使用
- 类型检查通过后 IR 与普通 generic 调用一致，零新指令

## Pipeline Steps

- [x] Lexer（复用 `class` / `struct` keyword）
- [ ] Parser / AST（GenericConstraint.Kinds flag 集合）
- [ ] TypeChecker（bundle flag + 校验）
- [x] IR Codegen（无改动）
- [x] VM interp（无改动）
