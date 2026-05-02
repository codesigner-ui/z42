# Spec: D1c — stdlib `Action` / `Func` / `Predicate` 真实类型 + 移除 hardcoded desugar

> 泛型 delegate 解析 + 实例化已在 D1a 完成（user 2026-05-02 裁决合并）。本 spec 仅
> 关注 stdlib 真实类型创建 + 移除 SymbolCollector hardcoded `"Action"` / `"Func"` 分支。

## ADDED Requirements

### Requirement: stdlib `Action` / `Func` / `Predicate` 由真实 delegate 声明提供

`z42.core` 包含真实 delegate 声明（不再依赖 SymbolCollector 硬编码 desugar）。覆盖 0-4 arity 的 Action/Func，以及 `Predicate<T>`。

#### Scenario: stdlib Action 加载
- **WHEN** 用户代码 `using Std;` + `Action<int> a = (int x) => Console.WriteLine(x);`
- **THEN** `Action<int>` 解析到 stdlib 的 `delegate void Action<T>(T arg)`，等价 `Z42FuncType([int], void)`

#### Scenario: stdlib Func 加载
- **WHEN** `Func<int, string> f = (int x) => x.ToString();`
- **THEN** 解析到 `delegate R Func<T, R>(T arg)` → `Z42FuncType([int], string)`

#### Scenario: stdlib Predicate 加载（**v1 新增**）
- **WHEN** `Predicate<int> isEven = (int x) => x % 2 == 0;`
- **THEN** 解析到 `delegate bool Predicate<T>(T arg)` → `Z42FuncType([int], bool)`

#### Scenario: arity 0 delegate
- **WHEN** `Action a = () => Console.WriteLine(1);`
- **THEN** 解析到 `delegate void Action()` → `Z42FuncType([], void)`

#### Scenario: arity 2 / 3 / 4
- **WHEN** `Action<int, string> a = ...;` / `Func<int, int, int> f = ...;` 等
- **THEN** stdlib 提供对应 arity；解析正常

### Requirement: SymbolCollector hardcoded `Action` / `Func` desugar 移除

当 stdlib 真实 delegate 提供 Action/Func 后，SymbolCollector 的硬编码分支必须删除，**唯一来源**是 stdlib delegate 注册表。

#### Scenario: 不依赖硬编码的 desugar
- **WHEN** SymbolCollector.cs line 211, 248-253 删除 `"Action"` / `"Func"` 分支
- **THEN** 现有 LambdaTypeCheckTests 仍全绿（因为 stdlib delegate 提供等价类型）

## MODIFIED Requirements

### Requirement: SymbolCollector 不再硬编码 `Action` / `Func` desugar

**Before**: SymbolCollector.cs:211 + 248-253 写死 `"Action"` / `"Func"` 名字 desugar 为 `Z42FuncType`，与 D1a 的 SymbolTable.Delegates 通道双路径并存。

**After**: 删除 hardcoded 路径；`Action` / `Func` / `Predicate` 名字唯一来源是 stdlib `z42.core/src/Delegates.z42` 加载到 SymbolTable.Delegates。

## IR Mapping

无新 IR 指令。复用 LoadFn / LoadFnCached（D1b 已加）/ MkClos / CallIndirect。

## Pipeline Steps

- [x] Lexer / 关键字 — D1a 已完成
- [x] Parser / AST — D1a 已完成（含泛型 + where）
- [x] TypeChecker DelegateInfo + 实例化路径 — D1a 已完成
- [ ] TypeChecker — 删除 hardcoded `Action` / `Func` desugar
- [ ] stdlib（z42.core） — 新增 Delegates.z42
- [x] IR Codegen / VM — 无变更
