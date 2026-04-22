# Spec: stdlib 泛型类导出

## ADDED Requirements

### Requirement: TSIG section 携带 TypeParams

zpkg 的 TSIG section 必须为每个 `ExportedClassDef` 携带 `tp_count + tp_name_idx[]`，
读回后 `ExportedClassDef.TypeParams` 与写入时一致。

#### Scenario: 泛型类 TSIG 往返
- **WHEN** 编译 stdlib `Std.Collections.Stack<T>` 生成 zpkg；然后从 zpkg 读回
- **THEN** `ExportedClassDef.Name == "Stack"`, `TypeParams == ["T"]`

#### Scenario: 非泛型类 TSIG 往返
- **WHEN** 编译 `class Foo { }` 生成 zpkg
- **THEN** 读回 `ExportedClassDef.TypeParams == null`（或空 list），`tp_count == 0`

### Requirement: imported 泛型类可实例化

user 代码 `new Std.Collections.Stack<int>()` 或简写 `new Stack<int>()`（未与 local 冲突时）必须能通过 TypeChecker 并正确 codegen。

#### Scenario: 短名实例化（无冲突）
- **WHEN** 用户代码只有 `void Main() { var s = new Stack<int>(); }`；stdlib 含 `Std.Collections.Stack<T>`
- **THEN** TypeChecker 不报错；ObjNew instr 的 class_name 为 `Std.Collections.Stack`

#### Scenario: 调用导入类方法
- **WHEN** `var s = new Stack<int>(); s.Push(42); int n = s.Pop();`
- **THEN** IR 中 `s.Push(42)` 生成指向 `Std.Collections.Stack.Push` 的调用（Call 或 VCall 根据分派规则）
- **AND** 运行时返回 `42`

### Requirement: local class 覆盖 imported 同名

user 自定义 `class Stack` 必须能存在，覆盖 stdlib 的 Stack 引用。

#### Scenario: 非泛型 local Stack 覆盖
- **WHEN** user 写 `class Stack { Stack() { } void Push(int v) { } }`；stdlib 含 `Std.Collections.Stack<T>`
- **THEN** TypeChecker 不报 `duplicate class declaration`
- **AND** `new Stack()` 实例化本地 Stack，而非 stdlib 泛型版本
- **AND** `new Std.Collections.Stack<int>()`（若语言支持 qualified new）仍指向 stdlib（**超范围：qualified `new` 当前语法限制**）

#### Scenario: 泛型 local Stack 覆盖
- **WHEN** user 写 `class Stack<T> { ... }`；stdlib 含 `Std.Collections.Stack<T>`
- **THEN** `new Stack<int>()` 指向 local Stack

### Requirement: VM 对 imported 泛型类正确分派

#### Scenario: ObjNew 对 imported 类
- **WHEN** 执行 `obj.new @Std.Collections.Stack %0`
- **THEN** VM 在 type_registry 中找 `Std.Collections.Stack` 对应 TypeDesc；若未加载，通过 lazy loader 拉取

#### Scenario: VCall 对 imported 类实例
- **WHEN** stack（`Std.Collections.Stack<int>` 实例）调用 `Push(42)`；VCall 查 type_desc.name=`Std.Collections.Stack`
- **THEN** VCall composer → `Std.Collections.Stack.Push`；在 func_index 或 lazy_loader 找到目标函数

### Requirement: 现有 user Stack 不受影响

本次变更不得破坏现有使用 `Stack` 名字的用户 golden 测试。

#### Scenario: 既有 user Stack 继续工作
- **WHEN** 运行 `run/38_self_ref_class`（本地 non-generic `class Stack`）
- **THEN** 通过，仍走 local 实现

#### Scenario: 既有 user Stack<T> 继续工作
- **WHEN** 运行 `run/74_generic_stack`（本地 generic `class Stack<T>`）
- **THEN** 通过，仍走 local 实现

## Pipeline Steps

- [x] Lexer / Parser / AST（无改动）
- [ ] TypeChecker（SymbolCollector 冲突裁决 + ImportedSymbolLoader TypeParams 保留）
- [ ] IR Codegen（QualifyClassName 本地优先）
- [ ] zbc binary（无改动）
- [ ] zpkg TSIG section（加 TypeParams 字段）
- [ ] VM loader + VCall / ObjNew（qualified name 查找）
- [ ] stdlib `Stack<T>` / `Queue<T>` 启用
