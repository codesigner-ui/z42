# Spec: Reflection — Type.GetProperties()

## ADDED Requirements

### Requirement: PropertyInfo 反射对象

`Std.Reflection.PropertyInfo : MemberInfo` 描述一个属性，由 `Type.GetProperties()` 填充。只读子集（无 GetValue/SetValue）。

#### Scenario: 读写属性
- **WHEN** 类声明 `public int X { get; set; }`，反射其 `PropertyInfo`
- **THEN** `Name == "X"`，`PropertyType.Name == "int"`，`CanRead == true`，`CanWrite == true`

#### Scenario: 只读属性
- **WHEN** 类声明 `public string Name { get; }`（仅 getter）
- **THEN** `CanRead == true`，`CanWrite == false`，`PropertyType.Name == "string"`

#### Scenario: extern / native 属性（如 Type.Name）
- **WHEN** 属性以 `[Native(...)] extern T P { get; }` 声明
- **THEN** 仍按 `get_P` 派生为只读 PropertyInfo（VM 视角与普通 getter 一致）

### Requirement: Type.GetProperties()

`Type.GetProperties()` 返回该类型的全部属性（含继承），按 vtable / own_methods 顺序。

#### Scenario: 枚举属性
- **WHEN** 对带属性的类 `typeof(C).GetProperties()`
- **THEN** 返回每个 `get_<Name>` / `set_<Name>` 配对去重后的 PropertyInfo 数组；同名 get/set 合并为一条（CanRead && CanWrite）

#### Scenario: 继承属性
- **WHEN** 子类 `D : C`，`C` 有属性 `X`，`typeof(D).GetProperties()`
- **THEN** 结果包含继承自 `C` 的 `X`（与 GetFields/GetMethods 的 base-first 含继承语义一致）

#### Scenario: 无属性 / handle-less 类型
- **WHEN** 类无任何 `get_`/`set_` 方法，或对基础类型/数组（`typeof(int)` / `int[]` 的 Type）调用
- **THEN** 返回**空数组**（绝不 null、绝不 bail）

#### Scenario: 写专属性（仅 setter）
- **WHEN** 属性仅有 `set_<Name>`（1 参，无 getter）
- **THEN** `CanRead == false`，`CanWrite == true`，`PropertyType` 取 setter 参数类型

## MODIFIED Requirements

无（GetFields / GetMethods / GetMembers 行为不变；accessor 方法 `get_X`/`set_X` 仍照常出现在 GetMethods，与 C# 一致）。

## IR Mapping

无新 IR / 无 zbc 格式变更。属性纯运行期从已加载的 `TypeDesc.vtable` + `cold.own_methods` 方法名约定派生。

## Pipeline Steps

- [ ] Lexer —（不涉及）
- [ ] Parser / AST —（不涉及）
- [ ] TypeChecker —（不涉及；`GetProperties` 经 extern 方法签名解析，与既有 `GetMethods` 同路径）
- [ ] IR Codegen —（不涉及）
- [x] VM interp — 新 `__type_properties` builtin（runtime）
- [x] stdlib — `PropertyInfo` 类 + `Type.GetProperties()` extern
