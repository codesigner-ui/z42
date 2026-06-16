# Spec: Type 类别谓词（IsClass / IsInterface）

## ADDED Requirements

### Requirement: 接口产生 TYPE 条目

#### Scenario: typeof(接口) 有真句柄
- **WHEN** `typeof(IFoo)`（`interface IFoo {}`）
- **THEN** 解析到真 `TypeDesc` 句柄：`.Name == "IFoo"`、`.IsAbstract == true`（接口隐式 abstract）
  （此前接口无 TYPE 条目 → name-only synthetic，`Name` 可读但无句柄、`IsAbstract` 为 false）

### Requirement: IsInterface

#### Scenario: 接口
- **WHEN** `typeof(IFoo).IsInterface`
- **THEN** true

#### Scenario: 非接口
- **WHEN** `typeof(C).IsInterface`（class）/ `typeof(S)`（struct）/ `typeof(int)` / `typeof(int[])`
- **THEN** false

### Requirement: IsClass

#### Scenario: 普通类与记录
- **WHEN** `typeof(C).IsClass`（`class C`）/ `typeof(R).IsClass`（`record R`，引用型）
- **THEN** true（记录是 class）

#### Scenario: 非类
- **WHEN** `typeof(IFoo).IsClass`（接口）/ `typeof(S).IsClass`（struct）/ `typeof(int).IsClass`
- **THEN** false（接口 / 值类型 / 基元都非 class）

#### Scenario: 与 IsValueType 互补
- **WHEN** `typeof(S)`（`struct S`）
- **THEN** `IsClass == false` 且 `IsValueType == true`（既有行为不变）

## MODIFIED Requirements

### Requirement: 接口的 TYPE section 产出

**Before:** `IrGen` 只为 `cu.Classes` emit `IrClassDesc`；接口（`cu.Interfaces`）完全不产 TYPE 条目，
`typeof(IFoo)` 运行期回落为 name-only synthetic Type（无句柄）。

**After:** `IrGen` 额外为每个 `InterfaceDecl` emit 一个最小 `IrClassDesc`（`IsInterface=true`、
`IsAbstract=true`、无 base / 字段、带 TypeParams）。接口现在解析到真句柄；`class_flags` bit4 标记类别。

## IR Mapping

`class_flags: u8`（TYPE section 每类条目尾部，zbc 1.19）扩 bit4：

```
bit0 abstract | bit1 sealed | bit2 struct | bit3 record | bit4 interface | bit5..7 预留（bit5=enum 未来）
```

无新字段；接口产 TYPE 条目 = TYPE section 内容变化 → zbc 1.18→1.19 / zpkg 0.20→0.21。

## Pipeline Steps

- [ ] Parser / AST — 无（复用 `InterfaceDecl`）
- [ ] TypeChecker — 无
- [x] IR Codegen — `EmitInterfaceDesc` + Generate 迭代 `cu.Interfaces`；flags bit4
- [x] zbc writer/reader — flags bit4 round-trip + version bump
- [x] VM — `CLASS_FLAG_INTERFACE` + `builtin_type_is_class` / `builtin_type_is_interface`
- [x] stdlib — `Type.IsClass` / `IsInterface`
