# Spec: GC OOM Exception

## ADDED Requirements

### Requirement: `new C()` under strict OOM throws `Std.OutOfMemoryException`

#### Scenario: 对象分配失败 — 可被 catch
- **WHEN** strict OOM 开启（`max_heap_bytes` 已满），`new SomeClass()` 触发
  `alloc_object` 返 `Value::Null`
- **THEN** interp throw `Std.OutOfMemoryException`
- **AND** 脚本 `try { ... } catch (OutOfMemoryException e)` 可捕获
- **AND** `e.Message` 包含被分配类型的名称

#### Scenario: 数组分配失败 — 可被 catch
- **WHEN** strict OOM 开启，`new T[n]` 触发 `alloc_array` 返 `Value::Null`
- **THEN** interp throw `Std.OutOfMemoryException`
- **AND** 脚本可 catch

#### Scenario: 双重 OOM fallback — 不 panic
- **WHEN** OOM exception 对象本身也无法分配（`make_stdlib_exception` 返
  `Value::Null` 或出现错误）
- **THEN** interp throw `Value::Null` 作为 best-effort fallback
- **AND** interp 不 panic（消除 `unreachable!` 路径）

#### Scenario: 闭包 env 数组分配失败 — 不 panic
- **WHEN** strict OOM 开启，`MkClos` / `Call` closure env `alloc_array` 返 Null
- **THEN** OOM exception 被传播，interp 不触发 `unreachable!` panic

## MODIFIED Requirements

### Requirement: strict OOM interp 行为

**Before**: `alloc_object` / `alloc_array` 返 `Value::Null`；interp 直接
把 Null 写入目标寄存器，继续执行，NRE 稍后发生。

**After**: interp 在各 alloc callsite 检测 `Value::Null` → 构造并 throw
`Std.OutOfMemoryException`；`Value::Null` 不再写入目标寄存器。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — exec_object / exec_array / exec_call / exec_instr 改动
- [x] GC subsystem — 不变（alloc 返 Null 行为保留）
- [x] stdlib — 新增 `Std.OutOfMemoryException` 类

## IR Mapping

无新 IR 指令。
