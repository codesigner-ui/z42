# Spec: 传递接口闭包

## ADDED Requirements

### Requirement: GetInterfaces 含传递接口

#### Scenario: 类经接口继承
- **WHEN** `interface IA {}` `interface IB : IA {}` `class C : IB {}`，`typeof(C).GetInterfaces()`
- **THEN** 含 `IB`（直接）+ `IA`（经 IB 传递）

#### Scenario: 接口自身的传递基接口
- **WHEN** `typeof(IB).GetInterfaces()`
- **THEN** 含 `IA`

### Requirement: is / as / IsAssignableFrom 含传递接口

#### Scenario: 经接口继承到达
- **WHEN** `C c = new C(); c is IA`（`class C : IB`、`IB : IA`）
- **THEN** true（此前 false）

#### Scenario: IsAssignableFrom 传递
- **WHEN** `typeof(IA).IsAssignableFrom(typeof(C))`
- **THEN** true

#### Scenario: 直接接口不回归
- **WHEN** `c is IB`（直接声明）
- **THEN** true（行为不变）

#### Scenario: 无关接口
- **WHEN** `c is IUnrelated`
- **THEN** false

## MODIFIED Requirements

### Requirement: 接口的基接口持久化

**Before:** `ParseInterfaceDecl` 跳过 `interface IBar : IFoo` 的基接口（InterfaceDecl 无字段）；
`EmitInterfaceDesc` 接口块恒空 → 接口继承图缺失，传递闭包无从计算。

**After:** parser 捕获基接口 → `InterfaceDecl.BaseInterfaces`；`EmitInterfaceDesc` 写进接口 TYPE
条目的接口块（FQ 名，复用 #2/#3 结构）。运行期据此做传递聚合。

### Requirement: GetInterfaces / is / IsAssignableFrom 传递语义

**Before:** 只覆盖"类直接声明 + 类继承链聚合"，不展开接口的基接口。

**After:** 对每个接口递归其基接口（BFS 去重收敛）。`is`/`as` 两条 VM 路径（interp + JIT）同步。

## IR Mapping

无新 IR / wire 字段：复用接口 TYPE 条目的接口块（#2/#3）。接口条目接口块从空变非空 → **无格式 bump**，
仅 regen。

## Pipeline Steps

- [x] Parser / AST — `InterfaceDecl.BaseInterfaces` + ParseInterfaceDecl 捕获
- [x] IR Codegen — `EmitInterfaceDesc` 填接口块（FQ）
- [x] VM — `builtin_type_interfaces` 传递 BFS + `is_subclass_or_eq_td`/`is_subclass_or_eq` 传递查接口
- [ ] stdlib — 无（行为由 runtime 升级）
