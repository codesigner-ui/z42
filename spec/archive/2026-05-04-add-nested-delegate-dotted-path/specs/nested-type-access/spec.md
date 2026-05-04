# Spec: 嵌套 delegate dotted-path 类型访问

## ADDED Requirements

### Requirement: 类型表达式接受 `Outer.Inner` 形式

Parser 把 `Outer.Inner` 解析为 `MemberType(Left: NamedType("Outer"), Right: "Inner")` AST 节点；
`Outer.Inner.Deeper` 解析为左结合链 `MemberType(MemberType(NamedType("Outer"), "Inner"), "Deeper")`。

类型表达式上下文：变量声明类型 / 字段类型 / 方法参数类型 / 方法返回类型 / generic 实参 / cast / typeof。

#### Scenario: 字段类型 dotted-path

- **WHEN** 源代码 `class App { Btn.OnClick handler; }`，其中 `class Btn { public delegate void OnClick(int x); }`
- **THEN** Parser 接受；TypeChecker 把 `handler` 字段类型 resolve 到 `Btn.OnClick` delegate signature

#### Scenario: 方法参数 / 返回类型 dotted-path

- **WHEN** `Btn.OnClick MakeHandler(Btn.OnClick base) { ... }`
- **THEN** 编译通过，参数与返回类型均解析正确

#### Scenario: 不存在的嵌套名报 E0401

- **WHEN** `Btn.NonExistent x;`（`Btn` 是 class 但没有 `NonExistent` 成员）
- **THEN** TypeChecker 报 E0401：``type `Btn` has no nested type `NonExistent` ``

#### Scenario: 左侧不是 class 报清晰错误

- **WHEN** `int.NotAType x;`（左侧是基础类型）
- **THEN** TypeChecker 报错：``cannot access nested type on non-class type `int` ``

### Requirement: 类内部仍可用 simple name 引用嵌套 delegate

`Btn` 类内部（同一类的成员体内）继续支持 `OnClick handler;` simple name 引用（不强制 `Btn.OnClick`）。本变更只**新增**外部 dotted-path 路径，不改变现有 simple-name 路径行为。

#### Scenario: 类内部 simple name 通过

- **WHEN** Btn 内部方法 `void Bind(OnClick h) { ... }`
- **THEN** 通过；`OnClick` resolve 到当前 class scope 的 nested delegate（D1a 行为）

## IR Mapping

无新 IR 指令。MemberType 在 TypeChecker 阶段被 resolve 到 delegate signature（与 NamedType 殊途同归），后续 IR 生成共用同一路径。

## Pipeline Steps

- [x] Lexer（无变化）
- [x] Parser / AST（加 MemberType；TypeParser dotted-path lookahead）
- [x] TypeChecker（SymbolTable.ResolveType 加 MemberType 分支）
- [x] IR Codegen（无变化 —— resolve 后 type 实例与 NamedType 同形）
- [x] VM interp（无变化）
