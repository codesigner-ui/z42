# Spec: D2a — MulticastAction<T>

## ADDED Requirements

### Requirement: `MulticastAction<T>` is a sealed generic class in `Std`

#### Scenario: 类型可见
- **WHEN** `using Std;` + `var bus = new MulticastAction<int>();`
- **THEN** 编译通过；`bus.Count == 0`

#### Scenario: 单 handler 订阅 + 触发
- **WHEN** `bus.Subscribe((int x) => Console.WriteLine(x)); bus.Invoke(42);`
- **THEN** 打印 "42"

#### Scenario: 多 handler 按 FIFO 触发
- **WHEN** `bus.Subscribe(h1); bus.Subscribe(h2); bus.Invoke(1);`
- **THEN** h1 先 触发，h2 后触发（注册顺序 = 调用顺序，对齐 delegates-events.md §4.2 I8）

### Requirement: Subscribe returns `IDisposable` token

订阅返回的 token 调用 `Dispose()` 等价于 `Unsubscribe(handler)`。

#### Scenario: dispose token 移除订阅
- **WHEN** `var token = bus.Subscribe(h); token.Dispose(); bus.Invoke(1);`
- **THEN** h 不被触发

#### Scenario: `using` 语法包装订阅
- **WHEN** `using (bus.Subscribe(h)) { bus.Invoke(1); } bus.Invoke(2);`
- **THEN** 第一次 Invoke 调 h；第二次不调（block 退出 Dispose）

### Requirement: `Unsubscribe` removes by reference equality

#### Scenario: 显式取消订阅
- **WHEN** `var h = (int x) => {}; bus.Subscribe(h); bus.Unsubscribe(h); bus.Invoke(1);`
- **THEN** h 不被触发

#### Scenario: 重复 Unsubscribe 是 no-op
- **WHEN** `bus.Unsubscribe(notSubscribed);`
- **THEN** 不抛错

### Requirement: COW snapshot — invoke 期间 subscribe / unsubscribe 不影响本次触发

#### Scenario: invoke 期间 subscribe 新 handler 不本次触发
- **WHEN** ```z42
  bus.Subscribe(x => bus.Subscribe(y => Console.WriteLine("inner")));
  bus.Invoke(1);
  ```
- **THEN** 仅打印 "inner" 0 次（外层 invoke 已 snapshot）；下次 `bus.Invoke(...)` 才会触发新 handler

#### Scenario: invoke 期间 unsubscribe self 仍触发本次
- **WHEN** ```z42
  Action<int>? self = null;
  self = (int x) => { bus.Unsubscribe(self!); Console.WriteLine("once"); };
  bus.Subscribe(self);
  bus.Invoke(1); bus.Invoke(2);
  ```
- **THEN** 第一次 invoke 打印 "once"（snapshot 已包含 self）；第二次不打印（已 unsubscribe）

### Requirement: Default `Invoke` is fail-fast (continueOnException=false)

`continueOnException` 默认值为 false；首个抛出的异常**直接传播**（不包装 MulticastException），与 C# delegate 行为对齐（K6）。

#### Scenario: 单 handler 抛异常 → 直接传播
- **WHEN** `bus.Subscribe(_ => throw new ArgumentException("boom")); bus.Invoke(0);`
- **THEN** 调用方接到 `ArgumentException`，不是 `MulticastException`

#### Scenario: 多 handler 一抛即停
- **WHEN** `bus.Subscribe(h_ok); bus.Subscribe(h_throws); bus.Subscribe(h_after);`
- **WHEN** `bus.Invoke(0);`
- **THEN** h_ok 触发；h_throws 抛 → 异常向上传；h_after **不触发**

#### Scenario: continueOnException=true 在 D2a 暂不支持
- **WHEN** 用户写 `bus.Invoke(0, continueOnException: true);` 且发生异常
- **THEN** D2a 行为同 false（fail-fast）；D2d 完成后改为聚合 `MulticastException` 抛出

### Requirement: `MulticastAction<T>` 可作为字段类型

类内部字段可声明 `MulticastAction<T>` 类型；字段必须显式 `new MulticastAction<T>()` 初始化（D2c 后 `event` 关键字会自动初始化）。

#### Scenario: 类字段
- **WHEN** ```z42
  class Btn { public MulticastAction<int> Clicked = new MulticastAction<int>(); }
  ```
- **THEN** 编译通过；可对 `btn.Clicked.Subscribe(...)` 操作

## MODIFIED Requirements

无（D2a 是新增类型 + stdlib 文件，无既有契约调整）。

## IR Mapping

无新 IR 指令。复用：
- `ObjNew` —— `new MulticastAction<int>()` 构造
- `VCall` —— `bus.Subscribe(...)` / `bus.Invoke(...)` 调用
- `CallIndirect` —— invoke 内部 dispatch handler

## Pipeline Steps

- [x] Lexer / Parser / AST — 无影响（D2c 加 event 关键字）
- [x] TypeChecker — 无影响（generic class 路径已成熟）
- [x] IR Codegen / VM — 无影响
- [ ] stdlib（z42.core）— 新增 `MulticastAction.z42`
