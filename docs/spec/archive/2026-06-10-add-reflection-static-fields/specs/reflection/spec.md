# Spec: Reflection — GetFields() 含静态字段 + FieldInfo.IsStatic

## ADDED Requirements

### Requirement: FieldInfo.IsStatic

`FieldInfo.IsStatic` 标识该字段是否 `static`。

#### Scenario: 实例字段
- **WHEN** `class C { public int x; }`，反射 `x` 的 FieldInfo
- **THEN** `IsStatic == false`

#### Scenario: 静态字段
- **WHEN** `class C { public static int count; }`，反射 `count` 的 FieldInfo
- **THEN** `IsStatic == true`

### Requirement: GetFields() 含静态字段

`Type.GetFields()` 返回实例字段（base-first，含继承）**之后**追加该类声明的静态字段。

#### Scenario: 实例 + 静态混合
- **WHEN** `class Account { public int balance; public static int count; }`，`typeof(Account).GetFields()`
- **THEN** 返回 2 条：`balance`（IsStatic=false）+ `count`（IsStatic=true）；各自 `FieldType` 正确

#### Scenario: 仅静态字段
- **WHEN** `class Config { public static string env; }`，GetFields()
- **THEN** 含 `env`（IsStatic=true，FieldType=string）

#### Scenario: handle-less 类型
- **WHEN** 对基础类型/数组的 Type 调用
- **THEN** 返回空数组（绝不 bail）

#### Scenario: 静态字段往返一致
- **WHEN** `static int count` 编译→zbc→VM 加载
- **THEN** `TypeDescCold.static_fields` 含 (`count`, `int`)；普通无静态字段的类 static_fields 为空

## MODIFIED Requirements

### Requirement: zbc / zpkg 格式版本
**Before:** zbc 1.12 / zpkg 0.14（TYPE section 无静态字段）。
**After:** zbc 1.13 / zpkg 0.15（TYPE section 每类在 flags 字节后追加 `static_field_count: u16` + 静态字段块）。strict-pin，全量 fixture + stdlib regen。

### Requirement: FieldInfo 结构
**Before:** `FieldInfo { Name, FieldType }`。
**After:** `FieldInfo { Name, FieldType, IsStatic }`。所有现有实例字段反射点 `IsStatic=false`（行为不变）。

## IR Mapping

- 无新 IR 指令。**zbc TYPE section wire 变更**：每类 flags 字节后追加 `static_field_count: u16` + 每条 (`name: u32`, `type_tag: u8`, `type_str: u32`)。
- 版本：zbc 12→13，zpkg 14→15（联动）。

## Pipeline Steps

- [ ] Lexer / Parser —（不涉及；`FieldDecl.IsStatic` 已有）
- [x] IR Codegen — `IrGen.EmitClassDesc` 填 `StaticFields`；`IrClassDesc` 新字段
- [x] zbc 序列化 — ZbcWriter 写 / ZbcReader 读静态字段块 + 版本 bump
- [x] VM 加载 — `read_type` → `ClassDesc.static_fields` → `build_type_registry` → `TypeDescCold.static_fields`
- [x] VM interp — `builtin_type_fields` 追加静态 + `IsStatic` 槽
- [x] stdlib — `FieldInfo.IsStatic`
