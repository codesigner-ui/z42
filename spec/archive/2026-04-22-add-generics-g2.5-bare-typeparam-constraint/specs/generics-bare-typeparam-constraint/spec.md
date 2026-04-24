# Spec: 裸类型参数约束（Bare Type Parameter Constraint）

## ADDED Requirements

### Requirement: `where U: T` 语法识别

当约束位置的 TypeExpr 解析出的名字恰好是同 decl 的其他 type parameter，TypeChecker 识别为裸类型参数约束。

#### Scenario: 函数裸参数约束
- **WHEN** `void F<T, U>(T t, U u) where U: T { }`
- **THEN** TypeChecker 将 `U` 的 bundle `TypeParamConstraint = "T"`（不是 class / interface）

#### Scenario: 类裸参数约束
- **WHEN**
  ```z42
  class Container<T, U> where U: T {
      T Get(U child) { return child; }
  }
  ```
- **THEN** `Container.TypeParamConstraints[U].TypeParamConstraint == "T"`

#### Scenario: 组合：U 约束 T 同时 + 接口
- **WHEN** `void F<T, U>(T t, U u) where U: T + IDisplay { u.Show(); }`
- **THEN** U 的 bundle 同时含 `TypeParamConstraint="T"` 和 `Interfaces=[IDisplay]`；成员查找组合生效

#### Scenario: 未声明的类型参数引用 → 当普通名字处理
- **WHEN** `void F<T>(T x) where T: Unknown { }`（Unknown 不在 type params 列表）
- **THEN** Unknown 解析为 class/interface（若注册了）或报 `TypeMismatch`（如 L3-G2 行为），不误判为 type-param

### Requirement: 受约束 U 的隐式上转

U 约束为 T 时，体内 U 类型值可赋给 T 类型位置（参数、返回值、字段）。

#### Scenario: U 返回为 T
- **WHEN** `class Container<T, U> where U: T { T Get(U child) { return child; } }`
- **THEN** 返回语句中 `child: U` 赋给 `T` 返回位置合法

#### Scenario: U 传给接受 T 的方法
- **WHEN** `void G<T>(T x) { } void F<T, U>(U u) where U: T { G(u); }`
- **THEN** 调用 `G(u)` 合法（u 为 U 但满足 T）

### Requirement: 调用点子类型校验

调用/实例化时，所有 typeArg 推断完成后，校验 `typeArg[U]` 是 `typeArg[T]` 的子类型（或相等）。

#### Scenario: 子类 typeArg 满足
- **WHEN** `class Animal { } class Dog : Animal { }` 和 `void F<T, U>(T t, U u) where U: T { }`
- **AND** `F<Animal, Dog>(a, d)` 或 `F(a, d)`（推断 T=Animal, U=Dog）
- **THEN** 类型检查通过

#### Scenario: 同类 typeArg 满足
- **WHEN** 同上，调用 `F<Animal, Animal>(a1, a2)`
- **THEN** 类型检查通过

#### Scenario: 非子类 typeArg 违反
- **WHEN** `class Animal { } class Vehicle { }` 和 `void F<T, U>(T t, U u) where U: T { }`
- **AND** 调用 `F<Animal, Vehicle>(a, v)`
- **THEN** 报错 `E0402 TypeMismatch`：`type argument 'Vehicle' for 'U' does not satisfy constraint 'T' (inferred 'Animal')`

### Requirement: 成员查找跨 type-param 跳转

U 的成员查找：先查 U 自身 interface 约束（若有）；若仍未命中，查 T 的 bundle（U 视为 T 的子类型，T 的成员即 U 的成员）。

#### Scenario: U 调用 T 的基类方法
- **WHEN** `class Base { public int Value; } void F<T, U>(U u) where T: Base, U: T { var v = u.Value; }`
- **THEN** 类型检查通过；`u.Value` 走 T 的 base-class 约束找到 Base.Value

#### Scenario: U 调用 T 的接口方法
- **WHEN** `interface IDisp { void Show(); } void F<T, U>(U u) where T: IDisp, U: T { u.Show(); }`
- **THEN** 类型检查通过

### Requirement: zbc 元数据 round-trip

`TypeParamConstraint` 字段完整写入 zbc、VM 读取后对齐。

#### Scenario: round-trip 保真
- **WHEN** `void F<T, U>(T t, U u) where U: T { }` 编译 → 写 zbc → 读 zbc
- **THEN** 读回 Function.TypeParamConstraints[U].TypeParamConstraint == "T"

#### Scenario: VM 加载 verify pass 放行
- **WHEN** VM 加载含裸 type-param 约束的 zbc
- **THEN** `verify_constraints` 不报错（裸 type-param 引用的永远是同一 decl 的 type-param，本地可解）

## IR Mapping

- zbc bundle 布局新增：
  ```
  flags: u8
    bit 0: RequiresClass
    bit 1: RequiresStruct
    bit 2: HasBaseClass
    bit 3: HasTypeParamConstraint   (new L3-G2.5 bare-typeparam)
    bit 4..7: reserved
  [if bit 2] base_class_name_idx: u32
  [if bit 3] type_param_name_idx: u32
  interface_count: u8
  interface_name_idx[]: u32
  ```
- zbc 版本 bump 0.5 → 0.6

## Pipeline Steps

- [x] Lexer（无改动）
- [x] Parser / AST（无改动）
- [ ] TypeChecker（bundle 字段 + 解析识别 + 成员查找 + 调用点校验）
- [ ] IR Codegen（bundle 新字段拷贝）
- [ ] zbc Writer / Reader（格式扩展 + 版本 bump）
- [ ] VM bytecode（镜像字段）
- [ ] VM loader + verify pass（无特殊规则）
