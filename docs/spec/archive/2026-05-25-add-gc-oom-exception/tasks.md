# Tasks: GC OOM Exception

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：vm

**总体策略**：`alloc_object`/`alloc_array` 返 Null = strict OOM 信号；各
interp callsite 检测并 throw `Std.OutOfMemoryException`；修复 mk_clos
unreachable! panic。Pure behaviour addition, no GC API change.

## 进度概览

- [x] 阶段 1-6: spec 文档
- [x] 阶段 6.5: User 确认
- [x] 阶段 7: 实施 P0
- [x] 阶段 8: GREEN
- [x] 阶段 9: 归档

## P0: 实施 (~1 session)

- [x] P0.1 NEW `src/libraries/z42.core/src/Exceptions/OutOfMemoryException.z42`
- [x] P0.2 MODIFY `src/runtime/src/interp/exec_object.rs`:
       - `obj_new` alloc_object 后：Null → make_stdlib_exception fallback
- [x] P0.3 MODIFY `src/runtime/src/interp/exec_array.rs`:
       - `array_new` / `array_new_lit` 返回类型 `Result<()>` →
         `Result<Option<Value>>`
       - 各自 alloc_array 后加 OOM check
- [x] P0.4 MODIFY `src/runtime/src/interp/exec_instr.rs`:
       - `ArrayNew` / `ArrayNewLit` caller → 处理 `Ok(Some(exc))`
       - `MkClos` caller（若 mk_clos 返回类型变更）→ 同步处理
- [x] P0.5 MODIFY `src/runtime/src/interp/exec_call.rs`:
       - `mk_clos` 返回类型 `Result<()>` → `Result<Option<Value>>`
       - `unreachable!` → OOM check + Ok(Some(exc))
       - `call()` env alloc（line ~161）→ OOM check
- [x] P0.6 NEW golden test `src/tests/gc/gc_oom_exception/`:
       - `source.z42` + `expected_output.txt`
- [x] P0.7 `cargo build --lib` GREEN
- [x] P0.8 `test-all.sh --scope=full` GREEN
- [x] P0.9 Commit P0

## P1: 文档 + 归档

- [x] P1.1 MODIFY `docs/design/runtime/gc.md`:
       - B1 条目状态 → ✅ 已落地
       - Phase 路线表加行 add-gc-oom-exception
       - 加 JIT-mode OOM Deferred 条目
- [x] P1.2 Archive → `docs/spec/archive/2026-05-25-add-gc-oom-exception/`
- [x] P1.3 Final `test-all.sh --scope=full` GREEN
- [x] P1.4 Commit + push

## 备注

- 实施期：HttpServer.z42 (E0402 _dispatchOne + ServeThreaded) + YamlValue.z42 (cast fix) 作为 build-stdlib GREEN 前置修复纳入同一 commit
- JIT 路径 OOM 检测延后：见 gc.md Deferred 段 `gc-oom-jit-path`
