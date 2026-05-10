# Spec: event 关键字 — IDisposable token + 严格 access control

## ADDED Requirements

### Requirement: 单播 event `add_X` 返回 IDisposable

每个声明为 `event Action<T> X;` 的单播 event 字段，其编译期合成的 `add_X(Action<T> h)` 返回类型从 `void` 改为 `Std.IDisposable`。同时合成嵌套 sealed 类 `Disposable_X`（实现 `IDisposable.Dispose()` 调用 `remove_X(this.h)`，h 是构造时捕获的 handler 引用）。

#### Scenario: 单播 event 订阅返回可 Dispose 的 token

- **WHEN** 在外部代码写 `using (var t = btn.OnKey += h) { ... }` 或显式 `var t = btn.OnKey += h; ...; t.Dispose();`
- **THEN** 编译通过，运行时 `t.Dispose()` 触发 `remove_X` 把 `_X` 字段清空，等价于直接 `btn.OnKey -= h;`

#### Scenario: 二次 add 仍 throw（保持 D-7 主体行为）

- **WHEN** 第一个 `+=` 已绑定 handler 后再次 `+=` 第二个 handler
- **THEN** 抛 `InvalidOperationException`，与 D-7 v1 行为一致；返回 IDisposable 不改变 single-binding 语义

### Requirement: event field 外部 invoke / 赋值 报 E0414

`event` 字段（多播或单播）在拥有类的方法外部不允许：

- 直接调用 `obj.X.Invoke(...)` 或 `obj.X(...)`（即把 X 作为 callable 直接 invoke）
- 直接赋值 `obj.X = newAction`

只允许 `obj.X += h` / `obj.X -= h`（这两种走合成的 `add_X` / `remove_X`）。

类内部访问（同一类的方法体内）允许任意操作（包括 `this.X.Invoke(arg)` 触发事件）。

#### Scenario: 外部 invoke 报 E0414

- **WHEN** `class Btn { public event Action<int> OnClick; }`，外部代码写 `btn.OnClick.Invoke(1)` 或 `btn.OnClick(1)`
- **THEN** TypeChecker 报 E0414：``event field `OnClick` cannot be invoked outside `Btn`; raise it from inside the class``

#### Scenario: 外部赋值报 E0414

- **WHEN** 外部写 `btn.OnClick = new Action<int>((x) => ...);`
- **THEN** TypeChecker 报 E0414：``event field `OnClick` cannot be assigned outside `Btn`; use `+=` / `-=` instead``

#### Scenario: 类内部访问不报错

- **WHEN** Btn 内的方法写 `this.OnClick.Invoke(1)` 或 `this.OnClick = null`
- **THEN** TypeChecker 通过；event 封装仅约束外部

#### Scenario: 外部 `+=` / `-=` 不报错（路径仍走合成的 add_X / remove_X）

- **WHEN** 外部 `btn.OnClick += h` 或 `btn.OnClick -= h`
- **THEN** desugar 到 `btn.add_OnClick(h)` / `btn.remove_OnClick(h)`，TypeChecker 不报 E0414（这两个调用是允许的接口）

## IR Mapping

无新 IR 指令。单播 token 类合成产物用现有 `ObjNew` + `Call`，与多播 `MulticastSubscription<T>` 模式同构。

`add_X` 返回类型变 `IDisposable` 改变 IrFunction.RetType 字符串与 `CallInstr` dst 类型，无需新 opcode。

## Pipeline Steps

- [x] Lexer（无变化）
- [x] Parser / AST（TopLevelParser.Members.cs 单播路径合成 token 类 + 改 add_X 签名）
- [x] TypeChecker（FieldAccess + Assign 加 E0414 触发；EventFieldNames 已就绪）
- [x] IR Codegen（合成的 token 类与 add_X 走现有 class / method 路径）
- [x] VM interp（无新 opcode；token Dispose 走普通 vcall）
