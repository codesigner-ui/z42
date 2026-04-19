# Spec: L3-G1 泛型基础

## ADDED Requirements

### Requirement: 泛型函数

#### Scenario: 定义和调用泛型函数
- **WHEN** 源码包含 `T Identity<T>(T x) { return x; }`
- **THEN** Parser 生成 FunctionDecl.TypeParams = ["T"]
- **WHEN** 调用 `Identity<int>(42)`
- **THEN** TypeChecker 验证 int 满足参数类型
- **THEN** IrGen 生成一份共享代码（不特化）
- **THEN** VM 正常执行，输出 42

#### Scenario: 多类型参数
- **WHEN** `Pair<A,B> MakePair<A,B>(A first, B second) { ... }`
- **THEN** TypeParams = ["A", "B"]

### Requirement: 泛型类

#### Scenario: 定义和实例化泛型类
- **WHEN** 源码包含：
  ```z42
  class Box<T> {
      T value;
      Box(T v) { this.value = v; }
      T Get() { return this.value; }
  }
  ```
- **THEN** ClassDecl.TypeParams = ["T"]
- **WHEN** `var b = new Box<int>(42);`
- **THEN** TypeChecker 将 T 替换为 int 验证构造器参数
- **THEN** VM 创建 ScriptObject，TypeDesc.type_args = ["int"]

#### Scenario: 泛型类方法调用
- **WHEN** `var v = b.Get();`
- **THEN** TypeChecker 推断返回类型为 int（T=int）
- **THEN** VM 正常执行 vtable 分发

### Requirement: 泛型类型表达式

#### Scenario: 类型位置使用泛型
- **WHEN** `Box<int> b = new Box<int>(42);`
- **THEN** Parser 生成 `GenericType("Box", ["int"])` TypeExpr
- **THEN** TypeChecker 解析为具体的泛型实例化类型

### Requirement: 运行时类型信息

#### Scenario: typeof 和 is 检查
- **WHEN** `var b = new Box<int>(42);`
- **THEN** `b.GetType()` 返回 type name 包含 "Box" 
- **THEN** `b is Box<int>` 返回 true

### Requirement: disasm 输出

#### Scenario: disasm 显示泛型元数据
- **WHEN** `z42c disasm file.zbc`
- **THEN** 泛型函数显示 `.type_params T` 或 `.type_params K, V`

## Pipeline Steps

- [x] Lexer（无变化，`<` `>` 已存在）
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [ ] VM interp
