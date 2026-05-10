# Spec: Generic Interface Dispatch

## ADDED Requirements

### Requirement: Z42InterfaceType 携带 TypeParams

`Z42InterfaceType` 数据结构增加 `TypeParams: IReadOnlyList<string>?` 字段。
实例化时 `TypeArgs` 与 `TypeParams` 按 index 对应（`IEquatable<T>` 实例化为
`IEquatable<int>` 时 `TypeParams = ["T"]`、`TypeArgs = [int]`）。

#### Scenario: stdlib 接口加载后含 TypeParams
- **GIVEN** stdlib `interface IEquatable<T> { ... }` 加载
- **WHEN** `_interfaces["IEquatable"]` 取出
- **THEN** `TypeParams == ["T"]`
- **AND** `TypeArgs == null`（generic 形式，未实例化）

#### Scenario: 用户写 `IEquatable<int>` 类型表达式
- **GIVEN** stdlib IEquatable 已加载
- **WHEN** TypeChecker 解析 `IEquatable<int>`
- **THEN** 新建 Z42InterfaceType 含 `TypeParams = ["T"]`、`TypeArgs = [int]`

### Requirement: Generic interface method args 经 TypeArgs substitute

`obj.Method(...)` 当 `obj` 类型是 instantiated `Z42InterfaceType`（TypeArgs
非空），TypeChecker 必须用 `TypeParams ↔ TypeArgs` 配对建 substitution map，
对 method 签名做 SubstituteTypeParams 后再做 arg type 检查。

#### Scenario: IEquatable<int>.Equals(int) 编译通过
- **GIVEN** `class MyInt : IEquatable<int> { bool Equals(int other) { ... } ... }`
- **AND** `bool Check(IEquatable<int> e, int v) { return e.Equals(v); }`
- **WHEN** TypeCheck Check 函数体
- **THEN** `e.Equals(v)` 编译通过（method `Equals(T)` substitute 为 `Equals(int)`，与 v: int 匹配）
- **AND** 运行时 VCall 派发到 MyInt.Equals 工作

#### Scenario: 嵌套泛型接口 method 调用
- **GIVEN** `interface IComparer<T> { int Compare(T x, T y); }` + 用户类实现 `IComparer<int>`
- **WHEN** `IComparer<int> c = ...; c.Compare(1, 2)`
- **THEN** 编译通过；返回 int

### Requirement: ClassType → InterfaceType 赋值识别 TypeArgs

`MyClass : IEquatable<int>` 实现的类实例可赋值给 `IEquatable<int>` 变量；
但**不**可赋值给 `IEquatable<string>` 变量（TypeArgs 不匹配）。

#### Scenario: 同 TypeArgs 类与接口赋值通过
- **GIVEN** `class M : IEquatable<int> { ... }`
- **WHEN** 用户写 `IEquatable<int> e = new M();`
- **THEN** 编译通过

#### Scenario: 不同 TypeArgs 类与接口赋值报错
- **GIVEN** `class M : IEquatable<int> { ... }`
- **WHEN** 用户写 `IEquatable<string> e = new M();`
- **THEN** 编译报错（`cannot assign M to IEquatable<string>`）

#### Scenario: 无 TypeArgs 接口约束兼容（向后兼容）
- **GIVEN** `class M : IDisposable { void Dispose() {...} }` （IDisposable 非泛型）
- **WHEN** `IDisposable d = new M();`
- **THEN** 编译通过

### Requirement: BindMemberExpr Z42InterfaceType 分支同步

[BindMemberExpr](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs)
在 `Z42InterfaceType` 分支已识别 auto-property getter（Wave 2 #4 引入）；
本变更要求其 return type 也按 TypeArgs substitute。

#### Scenario: interface property `T Current { get; }` 经 instantiated 接口访问
- **GIVEN** `interface IEnumerator<T> { T Current { get; } }` + 用户类实现 `IEnumerator<int>`
- **WHEN** 经接口变量 `IEnumerator<int> it; var x = it.Current;`
- **THEN** `x` 推断为 int（不是 T）

## MODIFIED Requirements

### Requirement: Z42InterfaceType 构造点必传 TypeParams

**Before**: `new Z42InterfaceType(Name, Methods, TypeArgs?, StaticMembers?)`
**After**: `new Z42InterfaceType(Name, Methods, TypeArgs?, StaticMembers?, TypeParams?)`

新参数末位 + nullable，向后兼容；但所有泛型接口构造路径**必须**传入
TypeParams（否则 method dispatch 仍 broken）。

### Requirement: Z42InterfaceType.ToString

不变（仍按 TypeArgs 显示 `IEquatable<int>` 或 `IEquatable`）。
TypeParams 是内部字段，不影响展示。

## IR Mapping

不引入新 IR 指令；纯 TypeChecker 内部数据结构 + 算法修复。

## Pipeline Steps

- [ ] Lexer / Parser — 不涉及
- [x] **TypeChecker** — Z42InterfaceType 字段扩展 + method dispatch substitute + assignable check
- [ ] IR Codegen — 不涉及（生成的 BoundCall 已含正确具体类型）
- [ ] VM — 不涉及
