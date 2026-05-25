# Proposal: add-gc-oom-exception

## Why
strict OOM 模式下 `alloc_object` / `alloc_array` 返回 `Value::Null`；
interp 没有检查，继续把 Null 写入寄存器。后续字段访问触发
`NullReferenceException`，原始 OOM 信息彻底丢失。嵌入用户无法
用 `try/catch` 区分 OOM 与正常 Null。另：`exec_call.rs::mk_clos`
中 `unreachable!` 在 strict OOM 下会 panic。

## What Changes
- NEW `Std.OutOfMemoryException` 类（继承 `Exception`）
- interp `obj_new` / `array_new` / `array_new_lit` / `mk_clos` callsite：
  检测 `alloc_object` / `alloc_array` 返 Null → throw
  `Std.OutOfMemoryException`；双重 OOM fallback throw `Value::Null`
- `exec_call.rs::mk_clos` 中 `unreachable!("alloc_array must return
  Value::Array")` → 改为 OOM 传播（消除 strict 模式 panic）
- `exec_array.rs` `array_new` / `array_new_lit` 返回类型
  `Result<()>` → `Result<Option<Value>>`（与 obj_new 对齐）

## Scope

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/libraries/z42.core/src/Exceptions/OutOfMemoryException.z42` | NEW | `Std.OutOfMemoryException : Exception` |
| `src/libraries/z42.core/src/GC/GC.z42` | MODIFY | 新增 `Std.GC.SetMaxHeapBytes(long)` + `Std.GC.SetStrictOOM(bool)` |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | obj_new alloc_object 后加 OOM check |
| `src/runtime/src/interp/exec_array.rs` | MODIFY | array_new / array_new_lit 返回类型 + OOM check |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | ArrayNew/ArrayNewLit + MkClos caller 处理 Ok(Some(exc)) |
| `src/runtime/src/interp/exec_call.rs` | MODIFY | mk_clos unreachable! → OOM 传播；call_indirect env alloc OOM check |
| `src/runtime/src/corelib/gc.rs` | MODIFY | 新增 builtin_gc_set_max_heap_bytes + builtin_gc_set_strict_oom |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 2 个新 builtin |
| `src/tests/gc/gc_oom_exception/source.z42` | NEW | 端到端 golden test |
| `src/tests/gc/gc_oom_exception/expected_output.txt` | NEW | golden 预期输出 |
| `src/tests/gc/gc_oom_exception/interp_only` | NEW | JIT 跳过标记（JIT OOM 路径延后）|
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | z42.core file count 59 → 60 |

**只读引用**：
- `src/runtime/src/exception/mod.rs` — `make_stdlib_exception` API 用法
- `src/runtime/src/gc/arc_heap.rs` — strict OOM 触发条件
- `src/libraries/z42.core/src/Exceptions/Exception.z42` — 基类结构

## Out of Scope
- JIT 路径 OOM（JIT 不经过 exec_object/exec_array，延后）
- GC observer `GcEvent::OutOfMemory` 新用法（已有，不改）
- 非 strict 模式行为（alloc 不返 Null，无需变更）
- 非堆 alloc 路径（stack closure arena 不经 GC heap）
