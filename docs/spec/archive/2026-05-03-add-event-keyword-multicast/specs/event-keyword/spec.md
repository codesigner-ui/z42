# Spec: `event` 关键字 + 多播 `+=` / `-=` desugar

## ADDED Requirements

### Requirement: `event` 关键字 lexer + parser + AST

#### Scenario: lexer 识别 `event` 关键字
- **WHEN** 源文件含 `event MulticastAction<int> X;`
- **THEN** lexer emit `TokenKind.Event` token

#### Scenario: parser 接受 `event` modifier 在字段
- **WHEN** 类内 `public event MulticastAction<int> Clicked;`
- **THEN** AST 包含 `FieldDecl(Name="Clicked", Type=GenericType("MulticastAction", [int]), IsEvent=true)`

#### Scenario: event 在 vis + 非 vis modifiers 之后
- **WHEN** `public event ...` / `public static event ...`
- **THEN** parse 成功，IsEvent=true

### Requirement: 多播 event field auto-init + add/remove 合成

#### Scenario: 多播 event 字段自动初始化
- **WHEN** `class C { public event MulticastAction<int> X; }` 实例化 `var c = new C();`
- **THEN** `c.X` 字段非 null（auto-init 为 `new MulticastAction<int>()`）

#### Scenario: 合成 add_X 方法
- **WHEN** `event MulticastAction<int> X` 声明
- **THEN** 类 implicitly 具有 `add_X(Action<int> h): IDisposable` 方法，等价于 `this.X.Subscribe(h)` 返回 `IDisposable`

#### Scenario: 合成 remove_X 方法
- **WHEN** 同上
- **THEN** 类 implicitly 具有 `remove_X(Action<int> h)` 方法，等价于 `this.X.Unsubscribe(h)`（依赖 D-5 已落地）

### Requirement: `+=` / `-=` 在多播 event field 上 desugar

#### Scenario: `obj.X += h` 转 add_X 调用
- **WHEN** `c.X += h` 其中 `X` 是多播 event field
- **THEN** TypeChecker emit `BoundCall(c.add_X, [h])`，返回值 IDisposable 在 statement 上下文丢弃

#### Scenario: `obj.X -= h` 转 remove_X 调用
- **WHEN** `c.X -= h`
- **THEN** TypeChecker emit `BoundCall(c.remove_X, [h])`

#### Scenario: 类内部仍可直接 invoke
- **WHEN** 类方法内 `this.X.Invoke(arg)`（多播 event field）
- **THEN** 编译通过（内部访问不受限制；本 spec 不做严格 access control）

### Requirement: 单播 event field 暂不支持

#### Scenario: 单播 event 类型报清晰错误
- **WHEN** 类内 `event Action<int> Y;`（非 MulticastAction 类型）
- **THEN** TypeChecker / SymbolCollector 报错 "single-cast event not yet supported (D2c-singlecast pending)"
- **NOTE** keyword 仍合法，留待 Spec 2b `add-event-keyword-singlecast` 实施

## MODIFIED Requirements

### Requirement: FieldDecl AST 节点新增 IsEvent

**Before:** `FieldDecl(Name, Type, Visibility, IsStatic, Initializer, Span)`
**After:** `FieldDecl(Name, Type, Visibility, IsStatic, Initializer, Span, IsEvent=false)` —— 末尾参数默认 false

## Pipeline Steps

- [x] Lexer（加 `event` keyword）
- [x] Parser / AST（FieldDecl.IsEvent + parse modifier + AST 内 synthesize add/remove FunctionDecl）
- [x] TypeChecker（`+=` / `-=` desugar；不严格 access control）
- [ ] IR Codegen（不动 —— 用既有方法调用路径）
- [ ] VM interp（不动）
