# Spec: Reflection — Type.IsValueType / IsRecord

## ADDED Requirements

### Requirement: Type.IsValueType

`Type.IsValueType` 返回该类型是否为值类型（`struct`），读 zbc TYPE-section flags 字节 bit2。

#### Scenario: struct
- **WHEN** `struct Point { public int x; }`，`typeof(Point).IsValueType`
- **THEN** `true`

#### Scenario: class
- **WHEN** `class Foo { }`，`typeof(Foo).IsValueType`
- **THEN** `false`

#### Scenario: handle-less 类型
- **WHEN** 对基础类型 / 数组的 Type 调用
- **THEN** `false`（无句柄 → flags=0，绝不 bail）

### Requirement: Type.IsRecord

`Type.IsRecord` 返回该类型是否声明为 `record`，读 flags 字节 bit3。

#### Scenario: record
- **WHEN** `record Pair { public int a; public int b; }`，`typeof(Pair).IsRecord`
- **THEN** `true`

#### Scenario: 非 record
- **WHEN** `class Plain { }`，`typeof(Plain).IsRecord`
- **THEN** `false`

## MODIFIED Requirements

无（复用 `add-reflection-type-flags` 已写进 wire 的 struct/record 位；**无格式变更**，zbc 维持 1.14 / zpkg 0.16）。

## IR Mapping

无 wire 变更——struct/record 位由 `add-reflection-type-flags`（zbc 1.12）已写入 `TypeDesc.class_flags`，本变更只新增运行期读取 builtin。

## Pipeline Steps

- [ ] Lexer / Parser / Codegen / 格式 —（不涉及；位已在 wire）
- [x] VM interp — `__type_is_value_type` / `__type_is_record` builtin
- [x] stdlib — `Type.IsValueType` / `Type.IsRecord`
