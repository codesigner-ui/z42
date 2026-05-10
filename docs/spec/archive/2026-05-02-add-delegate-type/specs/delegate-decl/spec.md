# Spec: D1a — `delegate` 关键字 + 命名 delegate 类型

## ADDED Requirements

### Requirement: `delegate` keyword declares a named callable type

Source 中允许形如 `public delegate R Name(T arg, ...);` 的顶层声明，被识别为命名 callable 类型。

#### Scenario: 简单 delegate 声明
- **WHEN** `public delegate void OnClick(int x, int y);`
- **THEN** Parser 产生 `DelegateDecl(Name="OnClick", Params=[(int x), (int y)], ReturnType=void)`，TypeChecker 注册 `OnClick → Z42FuncType([int, int], void)`

#### Scenario: 返回值 delegate
- **WHEN** `public delegate int Reduce(int a, int b);`
- **THEN** TypeChecker 注册 `Reduce → Z42FuncType([int, int], int)`

#### Scenario: 无参 delegate
- **WHEN** `public delegate void Done();`
- **THEN** TypeChecker 注册 `Done → Z42FuncType([], void)`

#### Scenario: 嵌套 delegate（class body 内声明）
- **WHEN** ```z42
  public class Btn {
      public delegate void OnClick(int x, int y);
  }
  ```
- **THEN** Parser 把 `OnClick` 解析为 `Btn.NestedDelegates` 的成员；外部用 `Btn.OnClick` 引用类型
- **THEN** 内部使用直接 `OnClick` 引用即可（与 nested class 同款）

### Requirement: Generic delegate declaration parses

`delegate R Name<T1, T2, ...>(T1 a, ...);` 形式被 Parser 接受，含可选 `where T : ...` 约束子句。

#### Scenario: 单类型参数 delegate
- **WHEN** `public delegate void Action<T>(T arg);`
- **THEN** 产生 `DelegateDecl(Name="Action", TypeParams=["T"], Params=[(T arg)], ReturnType=void, Where=null)`

#### Scenario: 多类型参数 delegate（同名多 arity）
- **WHEN** 同时声明 `delegate void Action<T>(T)` 和 `delegate void Action<T1,T2>(T1, T2)`
- **THEN** SymbolTable 用 `Action$1` / `Action$2` key 区分，互不冲突

#### Scenario: 泛型 delegate 实例化
- **WHEN** `Func<int, int>` 出现在源代码中（且 stdlib 或本 CU 声明了 `delegate R Func<T,R>(T arg)`）
- **THEN** ResolveType 实例化 → `Z42FuncType([int], int)`

#### Scenario: 错误 arity 报错
- **WHEN** `Func<int>` 但 `Func` 声明为 `<T, R>`（2 参数）
- **THEN** TypeChecker 报错 "generic delegate `Func` expects 2 type arguments, got 1"

### Requirement: Generic delegate with where-clause

可声明带 where 约束的 generic delegate，约束在实例化时验证（与 generic class / func 同路径）。

#### Scenario: where 约束声明
- **WHEN** `public delegate R Convert<T, R>(T arg) where T : class;`
- **THEN** 产生 `DelegateDecl(... TypeParams=["T","R"], Where=[T: class])`

#### Scenario: where 约束在实例化时验证
- **WHEN** 上述 delegate + `Convert<int, string>` 使用（int 不是 class）
- **THEN** TypeChecker 报错（沿用 ValidateGenericConstraints 现有诊断）

### Requirement: Lambda assignable to named delegate type

Lambda 字面量可赋给与签名匹配的命名 delegate 变量。

#### Scenario: lambda → delegate
- **WHEN** `delegate int Sq(int x);` + `Sq f = (int x) => x * x;` + `var r = f(5);`
- **THEN** 编译通过；运行时 `r == 25`

#### Scenario: 类型不匹配的 lambda 拒绝
- **WHEN** `delegate void NoRet(int x);` + `NoRet f = (int x) => x;`（lambda 返回 int 而 NoRet 是 void）
- **THEN** TypeChecker 报错 TypeMismatch

### Requirement: Delegate variable invocation `d(args)`

delegate 类型变量可像普通函数那样用括号语法调用。

#### Scenario: 直接调用
- **WHEN** `Sq f = (int x) => x * x; f(7)`
- **THEN** 走现有 CallIndirect 路径（lambda 是 closure，无 capture 走 LoadFn → FuncRef → CallIndirect）；返回 49

#### Scenario: capturing lambda 通过 delegate 调用
- **WHEN** `int n = 5; Sq f = (int x) => x + n; f(10)`
- **THEN** lambda 被识别为 capturing → 走 MkClos 路径；返回 15

### Requirement: Named delegate and `(T) -> R` literal type are structurally equivalent

命名 delegate 与对应的 `(T) -> R` 字面量类型互转无错（结构等价）。

#### Scenario: literal → named
- **WHEN** `delegate int Sq(int x);` + `(int) -> int g = (int x) => x * x; Sq f = g; f(3)`
- **THEN** 编译通过；返回 9

#### Scenario: named → literal
- **WHEN** `Sq f = (int x) => x * x; (int) -> int g = f; g(4)`
- **THEN** 编译通过；返回 16

### Requirement: null delegate invocation behaves like null FuncRef

未赋值的 delegate var 调用应抛 `NullReferenceException`（与 `Value::Null.CallIndirect` 现有路径一致）。

#### Scenario: 未初始化 delegate
- **WHEN** `Sq f; f(1);`
- **THEN** 运行时抛 NullReferenceException（VM `bail!("CallIndirect: expected FuncRef / Closure / StackClosure, got Null")`）

## MODIFIED Requirements

### Requirement: SymbolTable 暴露 delegate 注册表

**Before**: SymbolTable 跟踪 Functions / Classes / Interfaces / EnumTypes / EnumConstants。

**After**: 新增 `IReadOnlyDictionary<string, Z42FuncType> Delegates` —— delegate 名 → 等价的 FuncType。`ResolveType` 命中 NamedType `nt.Name` 时优先查 `Delegates`，命中即返回该 FuncType；没命中再走现有 class / enum / interface 路径。

### Requirement: SymbolCollector hardcoded `Action`/`Func` desugar 与新机制并存

**Before**: SymbolCollector 写死 `"Action"` / `"Func"` desugar（line 211, 248-253）。

**After**: D1a 不删除 hardcoded 路径（避免破坏现有 LambdaTypeCheckTests）；新增的命名 delegate 通过 SymbolTable.Delegates 解析，与 hardcoded 路径独立。D1c 实施时一并清理。

## IR Mapping

**无新 IR 指令**。复用：
- `LoadFnInstr` — no-capture lambda → FuncRef
- `MkClosInstr` — capturing lambda → Closure / StackClosure
- `CallIndirectInstr` — delegate var 调用

## Pipeline Steps

- [ ] Lexer — 增加 `Delegate` token
- [ ] Parser / AST — `DelegateDecl` + `ParseDelegateDecl`
- [ ] TypeChecker — `SymbolTable.Delegates` + `ResolveType` 优先级
- [ ] IR Codegen — 无变更
- [ ] VM interp / JIT — 无变更
