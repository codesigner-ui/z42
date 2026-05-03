# Spec: corelib WeakRef builtins

## ADDED Requirements

### Requirement: `__obj_make_weak` 创建弱引用

#### Scenario: Object 弱化
- **WHEN** `make_weak(value)` 其中 value 是 `Value::Object`
- **THEN** 返回 `Value::Object`（包装 NativeData::WeakRef 的 WeakHandle 实例）

#### Scenario: Array 弱化
- **WHEN** value 是 `Value::Array`
- **THEN** 同上返回 WeakHandle

#### Scenario: 原子值不可弱化
- **WHEN** value 是 `Value::I64` / `Str` / `Bool` / `Char` / `FuncRef` / Closure / StackClosure / Null
- **THEN** 返回 Value::Null（design line 130：原子值无法弱引用）

### Requirement: `__obj_upgrade_weak` 升格弱引用

#### Scenario: 目标存活 → 返回原对象
- **WHEN** `upgrade_weak(handle)` 且原目标 Object 仍有强引用持有
- **THEN** 返回 `Value::Object` 指向原 Object（同 ptr_eq）

#### Scenario: 目标已被回收
- **WHEN** 原目标的所有强引用 dropped 后调用 upgrade
- **THEN** 返回 `Value::Null`

#### Scenario: 非 WeakHandle 输入容错
- **WHEN** 传入非 WeakHandle Object（NativeData != WeakRef）或 Null
- **THEN** 返回 Null（不报类型错）

### Requirement: `Std.WeakHandle` stdlib 包装

#### Scenario: stdlib API
- **WHEN** stdlib `WeakHandle.MakeWeak(obj)` / `WeakHandle.Upgrade(h)` 调用
- **THEN** 等价 builtin 行为；用户脚本可以构建 WeakRef wrapper

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] VM interp（核心修改 — corelib + NativeData variant）
