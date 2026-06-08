# Spec: 反射 MVP（只读类型检视）

## ADDED Requirements

### Requirement: Type 携带运行时类型句柄

#### Scenario: GetType 返回带句柄的 Type
- **WHEN** 对一个用户类实例 `obj` 调用 `obj.GetType()`
- **THEN** 返回的 `Std.Type` 对象的 `Name` 等于该类的非限定名、`FullName` 等于全限定名
- **AND** 该 Type 对象内部携带指向真实 `TypeDesc` 的句柄（`NativeData::TypeHandle`），后续成员查询基于它

#### Scenario: 基础类型/数组退化
- **WHEN** 对 `int` 值、`T[]` 数组或 `string` 调用 `GetType()`
- **THEN** 返回 synthetic `Std.Type`（`Name` 为 `Int32`/`Array`/`String` 等），其 `GetFields()`/`GetMethods()` 返回空数组（无句柄）

### Requirement: 字段反射

#### Scenario: 枚举实例字段
- **WHEN** 类 `Point { int X; int Y; }` 的实例调用 `GetType().GetFields()`
- **THEN** 返回 `FieldInfo[]`，含 `X`、`Y` 两项
- **AND** 每个 `FieldInfo.Name` 正确，`FieldInfo.FieldType` 返回 `Std.Type`（`FieldType.Name == "int"`），参照 C# `FieldInfo.FieldType : Type`

#### Scenario: 继承字段包含在内
- **WHEN** `Derived : Base`，`Base` 有字段 `a`，`Derived` 有字段 `b`，对 `Derived` 实例调用 `GetType().GetFields()`
- **THEN** 返回的 `FieldInfo[]` 同时包含 `a`（继承）与 `b`（声明），base 字段在前

### Requirement: 方法反射（Phase A：名字；Phase B：签名）

#### Scenario: 枚举方法名（Phase A）
- **WHEN** 类有方法 `Foo()` / `Bar(int)`，对其实例调用 `GetType().GetMethods()`
- **THEN** 返回 `MethodInfo[]`，每个 `MethodInfo.Name` 正确

#### Scenario: 方法签名（Phase B，契约门控）
- **WHEN** 对方法 `int Add(int a, int b)` 的 `MethodInfo` 调用 `GetParameters()` 与读 `ReturnType`
- **THEN** `GetParameters()` 返回 2 个 `ParameterInfo`（`Name`=`a`/`b`，`ParameterType` 为 `Std.Type`(`Name=="int"`)，`Position`=0/1）
- **AND** `ReturnType` 返回 `Std.Type`(`Name=="int"`)（参照 C# `MethodInfo.ReturnType : Type`）
- **AND** `IsStatic`/`IsVirtual`/`IsAbstract` 反映声明

### Requirement: 基类与泛型反射

#### Scenario: BaseType
- **WHEN** `Derived : Base`，对 `Derived` 的 Type 读 `BaseType`
- **THEN** 返回 `Base` 的 `Std.Type`；对无显式基类的类返回 `Std.Object` 的 Type 或 `null`（design 裁决）

#### Scenario: 泛型实参
- **WHEN** 对 `Box<int>` 实例调用 `GetType().GetGenericArguments()`
- **THEN** 返回含 `"int"` 的类型数组（读 `ScriptObject.type_args` / `TypeDesc.cold.type_args`）

### Requirement: 方法 IsStatic（方法级标志）

#### Scenario: 静态 vs 实例方法
- **WHEN** 类有 `static int Helper()` 与 `int Inst()`，反射其 `MethodInfo`
- **THEN** `Helper` 的 `IsStatic == true`、`Inst` 的 `IsStatic == false`（来源 `Function.is_static`）

> **类级 `Type.IsAbstract`/`IsSealed`/`IsStatic` 延后**（本 change Out of Scope）：运行时 `TypeDesc`/`ClassDesc` 未加载类修饰符标志（仅方法级 `Function.is_static` 在）。补这些需 loader 读取 TSIG `ExportedClassDef` 的 `IsAbstract`/`IsSealed`/`IsStatic`——记入 reflection.md Deferred，单独 follow-up（与 Phase B 方法签名不同，类标志当前**未**加载进运行时）。

## MODIFIED Requirements

### Requirement: Object.GetType 行为扩展

**Before:** `GetType()` 返回仅含 `__name`/`__fullName` 的 synthetic `Std.Type`（`id = UNRESOLVED`），无法枚举成员。

**After:** `GetType()` 返回携带真实 `TypeDesc` 句柄的 `Std.Type`；`__name`/`__fullName` 保留（向后兼容现有调用），新增成员查询能力。基础类型/数组仍退化为无句柄 synthetic Type。

## IR Mapping

无新 IR 指令。`Std.Type` / `*Info` 的成员方法为 `extern`，编译器沿用现有 `CallNative`/builtin 降解路径调用新 corelib builtins（`__type_*` / `__method_*`）。

## Pipeline Steps

受影响阶段（无新语法，故不涉及 Lexer/Parser/TypeChecker 新规则）：
- [ ] Lexer —（不涉及）
- [ ] Parser / AST —（不涉及）
- [ ] TypeChecker —（不涉及；Type/*Info 是普通 stdlib 类）
- [ ] IR Codegen —（不涉及；extern 方法走现有降解）
- [x] VM interp — 新增 corelib reflection builtins + GetType 句柄化
- [x] stdlib — z42.core 扩展 Type + 新增 Std.Reflection.* 类
