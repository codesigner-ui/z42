# Spec: Type 泛型/基元谓词

## ADDED Requirements

### Requirement: IsGenericType

#### Scenario: 泛型类型
- **WHEN** `typeof(Box<int>).IsGenericType`（`class Box<T>`）
- **THEN** true（该类型有类型参数）

#### Scenario: 非泛型
- **WHEN** `typeof(int).IsGenericType` / `typeof(SomePlainClass).IsGenericType`
- **THEN** false

> `IsGenericTypeDefinition` 收窄延后（typeof 不携带实例化 type args，开放定义与实例化运行期不可区分）。见 design.md Decision 2。

### Requirement: IsPrimitive

#### Scenario: 基元
- **WHEN** `typeof(int).IsPrimitive` / `typeof(bool).IsPrimitive` / `typeof(char).IsPrimitive` / `typeof(double).IsPrimitive`
- **THEN** true

#### Scenario: 非基元
- **WHEN** `typeof(string).IsPrimitive`（string 非基元）/ `typeof(SomeClass).IsPrimitive`
- **THEN** false

## MODIFIED Requirements

无（纯新增派生属性，不改既有行为，无 wire 变更）。

## IR Mapping

无（无格式 bump；三谓词运行期派生自已加载的 `TypeDescCold.type_params` / `type_args` + 类型名）。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / Codegen — 无
- [x] VM reflection builtin — `__type_is_generic` / `__type_is_primitive`
- [x] stdlib — Type.IsGenericType / IsPrimitive
