# Spec: Closure Monomorphization

## ADDED Requirements

### Requirement: Local var bound to a known function emits direct Call

When a local variable is initialised by a single static reference to a known function and never reassigned, calls through that variable must lower to a direct `Call`, not `CallIndirect`.

#### Scenario: 基本别名
- **WHEN** `void Helper() { … } void Main() { var f = Helper; f(); }`
- **THEN** Codegen emits `Call("Demo.Helper", [])` for `f();`，IR 中无 `CallIndirect` 涉及 `f`

#### Scenario: 多次调用复用别名
- **WHEN** 上述 `var f = Helper;` 后调用 `f(); f(); f();`
- **THEN** 三次都被单态化为 `Call("Demo.Helper", …)`

#### Scenario: 重赋值后退化为间接调用
- **WHEN** `var f = A; if (cond) { f = B; } f();`
- **THEN** Codegen 检测到非单赋值，emit `CallIndirect`（语义保留）

### Requirement: No-capture lambda invoked at call site emits direct Call

无捕获 lambda（lifted 为顶层 `__lambda_N`）作为变量被立即调用时，必须直接 `Call(<lifted-name>)`。

#### Scenario: lambda 立即调用
- **WHEN** `var sq = (int x) => x * x; var r = sq(5);`
- **THEN** Codegen emit `Call("<container>__lambda_0", [r5])`，无 LoadFn / CallIndirect

#### Scenario: 闭包字面量传值后调用
- **WHEN** `var sq = (int x) => x * x; var r = sq(5); r = sq(6);`
- **THEN** 每次 `sq(...)` 都单态化（仍同一 `__lambda_0`）

### Requirement: Closure literal with capture stays on indirect path

带捕获的 closure 字面量必须继续走 `MkClos + CallIndirect`，单态化不应错误地把捕获 closure 当成静态函数。

#### Scenario: 捕获 closure 不被错误单态化
- **WHEN** `int n = 5; var add = (int x) => x + n; var r = add(3);`
- **THEN** Codegen emit `MkClos`，调用走 `CallIndirect` 或专门的 `CallClosure`（保留现状）

### Requirement: Function-typed parameters stay on indirect path

参数声明为函数类型的 callee 编译期不可知，必须保留 `CallIndirect`。

#### Scenario: 函数参数
- **WHEN** `int Apply((int) -> int f, int x) => f(x);`
- **THEN** `f(x)` 编译为 `CallIndirect`，因为 `f` 来自参数

### Requirement: Top-level function reference is always resolvable

直接使用顶层函数名作为 callee（不经局部变量）必须 emit `Call`。这是当前已有行为，保留为回归测试。

#### Scenario: 直接调用顶层函数
- **WHEN** `void Helper() {} void Main() { Helper(); }`
- **THEN** Codegen emit `Call("Demo.Helper", [])`（已有行为，验证未破坏）

## MODIFIED Requirements

### Requirement: BoundIdent semantic shape

**Before**: `BoundIdent(string Name, Z42Type Type, Span Span, BoundCaptureKind? Capture, ...)` —— 没有携带"该 ident 是否解析为已知函数"。

**After**: `BoundIdent(string Name, Z42Type Type, Span Span, BoundCaptureKind? Capture, string? ResolvedFuncName, ...)` —— 当 TypeChecker 能在编译期解析 ident 为顶层函数 / 静态方法 / 已知 closure / 已知 lambda 时填入 fully-qualified name；否则 null。

### Requirement: EmitBoundCall callee resolution priority

**Before**: callee 是 BoundIdent 时按以下顺序：local var → field → top-level → import → 否则 CallIndirect。

**After**: 在该顺序之前先检查 `BoundIdent.ResolvedFuncName`：若非 null 直接发 `Call(ResolvedFuncName, args)`；否则继续原顺序。

## IR Mapping

无新 IR 指令。复用：
- `CallInstr(Dst, FuncName, Args)` — 单态化命中后发射
- `CallIndirectInstr(Dst, CalleeReg, Args)` — fallback，未命中时发射（语义不变）

## Pipeline Steps

- [x] Lexer — 无影响
- [x] Parser / AST — 无影响
- [ ] TypeChecker — 扩展 BoundIdent + 单赋值跟踪
- [ ] IR Codegen — callee 解析优先级
- [ ] VM interp / JIT — 无变更（fallback 路径已就位）
