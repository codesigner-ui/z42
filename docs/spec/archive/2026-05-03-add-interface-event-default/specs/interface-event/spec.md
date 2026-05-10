# Spec: interface event default 实现

## ADDED Requirements

### Requirement: interface body 接受 `event` modifier

#### Scenario: 多播 event 声明产生两个 method signatures
- **WHEN** interface 内 `event MulticastAction<int> Clicked;`
- **THEN** InterfaceDecl.Methods 含 `add_Clicked(Action<int>): IDisposable` + `remove_Clicked(Action<int>): void`，两者 IsStatic=false / IsVirtual=false / Body=null（instance abstract）

#### Scenario: 单播 event 在 interface 同样报 not-yet-supported
- **WHEN** interface 内 `event Action<int> OnKey;`
- **THEN** parser 报 "single-cast event not yet supported"（与 class 端一致）

### Requirement: class 实现 interface 多播 event

#### Scenario: class 声明同名 event 满足 interface 契约
- **WHEN** `interface IBus { event MulticastAction<int> Clicked; }` + `class Bus : IBus { public event MulticastAction<int> Clicked; }`
- **THEN** TypeChecker 认 Bus 实现 IBus（class 合成 add_Clicked/remove_Clicked 满足 interface 抽象 signature）

#### Scenario: 通过 interface 接收对象 `+=` / `-=` 工作
- **WHEN** `IBus b = new Bus(); b.Clicked += h;`
- **THEN** desugar 到 `b.add_Clicked(h)`，dispatch 到 Bus.add_Clicked（vtable）
- **AND** 同样 `b.Clicked -= h` → `b.remove_Clicked(h)`

## Pipeline Steps

- [ ] Lexer
- [x] Parser / AST（`event` 在 interface body）
- [ ] TypeChecker（不动 —— 既有 implementsinterface 检查路径覆盖）
- [ ] IR Codegen
- [ ] VM interp
