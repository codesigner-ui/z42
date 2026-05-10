# Spec: 基类约束（Base Class Constraint）

## ADDED Requirements

### Requirement: 基类约束语法

`where T: BaseClass` — 类型参数 T 须继承自（或等于）指定类。基类约束必须位于约束列表首位，后续可用 `+` 继续加 interface 约束。

#### Scenario: 单基类约束
- **WHEN** 源码为
  ```z42
  class Animal { public int legs; }
  void Describe<T>(T x) where T: Animal { Console.WriteLine(x.legs); }
  ```
- **THEN** 类型检查通过
- **AND** T 在 Describe 体内可访问 Animal 的字段

#### Scenario: 基类 + 接口组合
- **WHEN** 源码为 `void F<T>(T x) where T: Animal + IDisplay { x.legs; x.Show(); }`
- **THEN** 类型检查通过；`x.legs` 走基类字段，`x.Show()` 走接口方法

#### Scenario: 多基类报错
- **WHEN** 源码为 `void F<T>(T x) where T: Animal + Vehicle { }`（两者都是类）
- **THEN** 报错 `E0103 TypeMismatch`：`generic parameter T cannot have multiple class constraints`

#### Scenario: 基类不在首位报错
- **WHEN** 源码为 `void F<T>(T x) where T: IFoo + Animal { }`
- **THEN** 报错 `E0103 TypeMismatch`：`class constraint Animal must appear first in constraint list`

### Requirement: 基类约束体内成员访问

受基类约束的 T，其实例可访问基类字段、方法、静态方法。

#### Scenario: 基类字段访问
- **WHEN** `class Base { int x; }` 和 `void F<T>(T t) where T: Base { t.x; }`
- **THEN** 类型检查通过，生成 BoundMember 访问字段 x

#### Scenario: 基类方法调用
- **WHEN** `class Base { int Compute() { return 1; } }` 和 `void F<T>(T t) where T: Base { t.Compute(); }`
- **THEN** 类型检查通过，生成 VCall（按 T 实际类型分发）

#### Scenario: 基类方法 + 接口方法优先级
- **WHEN** `class Base { void M() { } }` 和 `interface IFoo { void M(); }` 和 `void F<T>(T t) where T: Base + IFoo { t.M(); }`
- **THEN** 类型检查通过；实现按声明顺序查找（基类 Base 的 M 优先）

### Requirement: 调用点约束校验

调用/实例化受基类约束的泛型函数或类时，类型参数必须是该基类或其子类。

#### Scenario: 子类满足约束
- **WHEN** `class Animal { }` 和 `class Dog : Animal { }` 和 `void F<T>(T t) where T: Animal { }`
- **AND** 调用 `F<Dog>(d)` 或 `F(new Dog())`（推断）
- **THEN** 类型检查通过

#### Scenario: 同类满足约束
- **WHEN** `class Animal { }` 和 `void F<T>(T t) where T: Animal { }`
- **AND** 调用 `F<Animal>(a)`
- **THEN** 类型检查通过

#### Scenario: 非子类违反约束
- **WHEN** `class Animal { }` 和 `class Vehicle { }` 和 `void F<T>(T t) where T: Animal { }`
- **AND** 调用 `F<Vehicle>(v)`
- **THEN** 报错 `E0402 TypeMismatch`：`type argument 'Vehicle' for 'T' does not satisfy constraint 'Animal'`

#### Scenario: 泛型类基类约束实例化
- **WHEN** `class Box<T> where T: Animal { T item; Box(T x) { this.item = x; } }`
- **AND** 调用 `new Box<Dog>(new Dog())`
- **THEN** 类型检查通过

## IR Mapping

- 约束**不**写入 zbc（同 L3-G2；L3-G3a 统一扩展）
- 基类字段访问 / 方法调用 → 现有 `FieldGet` / `VCallInstr`（按 T 实际类型的 TypeDesc 分发，VM 无改动）

## Pipeline Steps

- [x] Lexer（无新 token；复用 `where` / `+` / `,`）
- [x] Parser / AST（无新节点；TypeExpr 可携带类或接口）
- [ ] TypeChecker
- [x] IR Codegen（无改动）
- [x] VM interp（无改动）
