# Spec: Type 泛型定义/构造型反射

## ADDED Requirements

### Requirement: typeof 携带实例化 type args

#### Scenario: 构造型 GetGenericArguments
- **WHEN** `typeof(Box<int>).GetGenericArguments()`（`class Box<T>`）
- **THEN** 返回长度 1 的数组，元素 `== typeof(int)`（修复此前返回空数组的 bug）

#### Scenario: 多参构造型
- **WHEN** `typeof(Pair<int, string>).GetGenericArguments()`（`class Pair<K, V>`）
- **THEN** 返回 `[typeof(int), typeof(string)]`（顺序与声明一致）

#### Scenario: 非泛型 typeof 不受影响
- **WHEN** `typeof(Plain).GetGenericArguments()` / `typeof(int)` / `typeof(int[])`
- **THEN** 返回空数组；既有 `Name`/`BaseType`/`IsArray`/`GetElementType` 行为不变

### Requirement: IsGenericTypeDefinition

#### Scenario: 构造型不是定义
- **WHEN** `typeof(Box<int>).IsGenericTypeDefinition`
- **THEN** false（已构造，带 type args）

#### Scenario: 定义型是定义
- **WHEN** `typeof(Box<int>).GetGenericTypeDefinition().IsGenericTypeDefinition`
- **THEN** true（剥掉 type args 后的开放定义）

#### Scenario: 非泛型
- **WHEN** `typeof(Plain).IsGenericTypeDefinition`
- **THEN** false（非泛型既非定义也非构造）

### Requirement: GetGenericTypeDefinition

#### Scenario: 构造型取回定义
- **WHEN** `typeof(Box<int>).GetGenericTypeDefinition()`
- **THEN** 返回 `Box` 的定义型 `Std.Type`：`IsGenericType==true`、`IsGenericTypeDefinition==true`、
  `GetGenericArguments()` 为空、`Name == "Box"`

#### Scenario: 非泛型抛异常
- **WHEN** `typeof(Plain).GetGenericTypeDefinition()`
- **THEN** 抛 `InvalidOperationException`（对齐 C#：非泛型类型无定义）

## MODIFIED Requirements

### Requirement: GetGenericArguments 数据源

**Before:** `Type.GetGenericArguments()` 读 `TypeDesc.type_args()`；`typeof(Generic<T>)` 解析到
定义 TypeDesc（`type_args` 永远空）→ 返回空数组（与 `IsGenericType==true` 矛盾）。

**After:** 读 `Std.Type` 对象的运行期 `__typeArgs` 槽。typeof 的构造型在求值时把实例化 args
写入该槽 → `typeof(Box<int>).GetGenericArguments() == [typeof(int)]`。

## IR Mapping

新增 opcode `Typeof`（zbc 1.18 / zpkg 0.20）：

```
Typeof  dst_reg  TypeName:STRS_idx  arg_count  TypeArg:STRS_idx[arg_count]
```

- 替换原 `ConstStr + Builtin(__typeof)` 序列；`__typeof` builtin 移除。
- TypeArgs 编码与 `ObjNew` type_args 一致（count + STRS 索引列表）。
- 非泛型 typeof：`arg_count == 0`。

## Pipeline Steps

- [x] Parser / AST — 无（复用既有 `BoundTypeof` + `Z42InstantiatedType`）
- [x] TypeChecker — 无（`Z42InstantiatedType.TypeArgs` 已就绪）
- [x] IR Codegen — `VisitTypeof` emit `TypeofInstr`
- [x] zbc writer/reader — 新 opcode 序列化 + version bump
- [x] VM interp — `Instruction::Typeof` 求值 + type-args 槽
- [x] VM JIT — translate Typeof（runtime helper）
- [x] stdlib — `IsGenericTypeDefinition` / `GetGenericTypeDefinition`
