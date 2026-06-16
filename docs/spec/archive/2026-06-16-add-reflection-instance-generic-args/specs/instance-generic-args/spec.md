# Spec: 实例泛型 args

## ADDED Requirements

### Requirement: obj.GetType().GetGenericArguments()

#### Scenario: 泛型实例
- **WHEN** `Box<int> b = new Box<int>(); b.GetType().GetGenericArguments()`（`class Box<T>`）
- **THEN** 返回 `[typeof(int)]`（此前返空数组）

#### Scenario: 与 typeof 一致
- **WHEN** `b.GetType()`（`b = new Box<int>()`）vs `typeof(Box<int>)`
- **THEN** 两者都是构造型：`IsGenericType` true、`IsGenericTypeDefinition` false、
  `GetGenericArguments()` 均为 `[int]`

#### Scenario: 取定义
- **WHEN** `b.GetType().GetGenericTypeDefinition().GetGenericArguments()`
- **THEN** 空（开放定义）

#### Scenario: 非泛型实例不受影响
- **WHEN** `new Plain().GetType().GetGenericArguments()`
- **THEN** 空数组（行为不变）

## MODIFIED Requirements

### Requirement: __obj_get_type 对泛型实例产构造型 Type

**Before:** `obj.GetType()` 始终 `make_type_object(td)`（裸定义句柄，无 `__typeArgs`）→
`GetGenericArguments()` 返空（即便 `obj` 是 `new Box<int>()`）。

**After:** 当 `ScriptObject.type_args` 非空，构建构造型 Type（`make_constructed_type`，挂 `__typeArgs`
槽）→ `GetGenericArguments()` 返实例化 args。

## IR Mapping

无（纯运行期；`ScriptObject.type_args` 已由 ObjNew 写入，`GetGenericArguments` 已读 `__typeArgs` 槽）。

## Pipeline Steps

- [x] VM — `__obj_get_type` 泛型实例 → make_constructed_type
- [ ] 其余 — 无（无 wire / IR / stdlib 改动）
