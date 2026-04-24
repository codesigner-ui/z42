# Spec: 泛型约束（Interface Constraints）

## ADDED Requirements

### Requirement: `where` 子句语法

泛型函数、类、接口可在签名尾部声明类型参数约束。

#### Scenario: 单一接口约束
- **WHEN** 源码为
  ```z42
  T Max<T>(T a, T b) where T: IComparable<T> {
      return a.CompareTo(b) > 0 ? a : b;
  }
  ```
- **THEN** Parser 生成 `FunctionDecl.WhereClause.Constraints[0] = GenericConstraint("T", [IComparable<T>])`
- **AND** TypeChecker 不报错

#### Scenario: 多接口约束（`+` 分隔）
- **WHEN** 源码为 `void Log<T>(T x) where T: IDisplay + IEquatable<T> { ... }`
- **THEN** `WhereClause.Constraints[0].Constraints` 包含两个类型表达式 `IDisplay`, `IEquatable<T>`
- **AND** TypeChecker 将 T 绑定为 `Z42GenericParamType("T", Constraints: [IDisplay, IEquatable<T>])`

#### Scenario: 跨类型参数约束（`,` 分隔多 constraint entry）
- **WHEN** 源码为 `void Copy<K, V>(K k, V v) where K: IHashable, V: ICloneable { ... }`
- **THEN** `WhereClause.Constraints.Count == 2`，分别绑定 K 和 V

#### Scenario: 泛型类 where 子句
- **WHEN** 源码为
  ```z42
  class SortedList<T> where T: IComparable<T> {
      T[] items;
      void Add(T item) { ... }
  }
  ```
- **THEN** ClassDecl.WhereClause 挂载约束
- **AND** TypeChecker 在类字段/方法体内，T 带约束作用域

### Requirement: 约束方法调用

泛型函数/类体内，约束类型参数 T 的实例可调用约束接口的方法。

#### Scenario: 约束接口方法可调用
- **WHEN** 源码为 `T Max<T>(T a, T b) where T: IComparable<T> { return a.CompareTo(b) > 0 ? a : b; }`
- **THEN** `a.CompareTo(b)` 类型检查通过
- **AND** IrGen 生成 `VCallInstr`（运行时分发）

#### Scenario: 未约束方法调用报错
- **WHEN** 源码为 `void F<T>(T a) { a.CompareTo(a); }`（T 无约束）
- **THEN** 报错 `E0104 MemberNotFound`：`type parameter T has no method CompareTo`

### Requirement: 调用点约束校验

调用泛型函数/实例化泛型类时，类型参数必须实现所有约束接口。

#### Scenario: 类型参数满足约束
- **WHEN** 存在 `class MyClass : IComparable<MyClass> { ... }` 及 `T Max<T>(T a, T b) where T: IComparable<T>`
- **AND** 调用 `Max<MyClass>(x, y)`
- **THEN** TypeChecker 不报错

#### Scenario: 类型参数不满足约束
- **WHEN** 存在 `class Plain { }` 和 `T Max<T>(T a, T b) where T: IComparable<T>`
- **AND** 调用 `Max<Plain>(x, y)`
- **THEN** 报错 `E0103 TypeMismatch`：`type argument 'Plain' does not satisfy constraint IComparable<Plain> on T`

#### Scenario: 类型推断 + 约束校验
- **WHEN** 存在 `class MyClass : IComparable<MyClass>` 和 `T Max<T>(T a, T b) where T: IComparable<T>`
- **AND** 调用 `Max(x, y)` 其中 `x, y : MyClass`
- **THEN** 推断 T=MyClass，校验通过

#### Scenario: 多约束部分不满足
- **WHEN** `class Half : IDisplay {}` 和 `void F<T>(T x) where T: IDisplay + IEquatable<T>`
- **AND** 调用 `F<Half>(x)`
- **THEN** 报错 `E0103 TypeMismatch`：`type argument 'Half' does not satisfy constraint IEquatable<Half>`

### Requirement: 类体内 this 约束传播

泛型类 `class C<T> where T: I` 的方法内，T 保持约束。

#### Scenario: 类字段上调用约束方法
- **WHEN** 源码为
  ```z42
  class Sorted<T> where T: IComparable<T> {
      T first;
      int Rank(T other) { return this.first.CompareTo(other); }
  }
  ```
- **THEN** 类型检查通过
- **AND** 生成 VCallInstr

## IR Mapping

- 约束**不**写入 zbc 二进制（不在 SIGS/TYPE section 新增字段）
- 约束方法调用 → `VCallInstr`（与普通 interface 调用一致）
- 代码共享延续：`Max<MyClass>` 和 `Max<Other>` 共用一份 IR

## Pipeline Steps

- [x] Lexer（`where` 关键字已存在于 TokenDefs）
- [ ] Parser / AST（新增 WhereClause, GenericConstraint）
- [ ] TypeChecker（约束解析、作用域、方法查找、调用点校验）
- [ ] IR Codegen（无改动，沿用 VCall）
- [ ] VM interp（无改动）
