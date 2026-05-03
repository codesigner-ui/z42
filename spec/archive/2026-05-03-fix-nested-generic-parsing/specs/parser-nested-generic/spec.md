# Spec: 嵌套 generic 类型 parse

## ADDED Requirements

### Requirement: nested generic 类型在所有声明上下文可解析

#### Scenario: 字段声明嵌套 generic
- **WHEN** 类内字段声明 `Foo<Bar<T>> field;`
- **THEN** parser 成功生成 AST，字段类型节点为 `Foo<Bar<T>>`（嵌套 NamedType）

#### Scenario: 方法参数嵌套 generic
- **WHEN** 方法签名 `void m(Foo<Bar<T>> p)`
- **THEN** parser 成功，参数类型节点为 `Foo<Bar<T>>`

#### Scenario: 方法返回类型嵌套 generic
- **WHEN** 方法签名 `Foo<Bar<T>> get()`
- **THEN** parser 成功，返回类型为 `Foo<Bar<T>>`

#### Scenario: 局部变量类型嵌套 generic
- **WHEN** 函数体内 `Foo<Bar<T>> v = ...;`
- **THEN** parser 成功

### Requirement: nested generic 类型在表达式上下文可解析

#### Scenario: `new T[n]` 元素类型嵌套 generic
- **WHEN** 表达式 `new Foo<Bar<T>>[n]`
- **THEN** parser 成功，AST 表达式节点元素类型 `Foo<Bar<T>>`

#### Scenario: 三层及以上嵌套
- **WHEN** 类型 `Foo<Bar<Baz<T>>>`（3 个 `>` 连写）
- **THEN** parser 成功，递归构造嵌套 NamedType

### Requirement: 单 `Gt` 关闭仍正确

#### Scenario: 单层 generic 不受影响
- **WHEN** `Foo<int>` / `Foo<T>` / `List<string>` 等单层
- **THEN** 行为与修复前一致；现有所有测试不破坏

## Pipeline Steps

受影响阶段：

- [x] Lexer（**不修改**，仍 emit `GtGt`；仅作为前置确认）
- [x] Parser / AST（核心修改）
- [ ] TypeChecker（不动）
- [ ] IR Codegen（不动）
- [ ] VM interp（不动）
