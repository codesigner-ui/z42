# Tasks: Add MagrGC Heap Interface

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-29

## 进度概览
- [x] 阶段 1: 接口定义
- [x] 阶段 2: RcMagrGC 实现
- [x] 阶段 3: VmContext 集成
- [x] 阶段 4: callsite 迁移
- [x] 阶段 5: 文档同步
- [x] 阶段 6: 验证

## 阶段 1: 接口定义
- [x] 1.1 新建 `src/runtime/src/gc/heap.rs` —— `trait MagrGC` + `HeapStats`
- [x] 1.2 新建 `src/runtime/src/gc/heap_tests.rs` —— trait 默认方法契约测试（3 个）
- [x] 1.3 修改 `src/runtime/src/gc/mod.rs` —— 移除 stub，re-export `MagrGC` / `HeapStats` / `RcMagrGC`

## 阶段 2: RcMagrGC 实现
- [x] 2.1 新建 `src/runtime/src/gc/rc_heap.rs` —— `RcMagrGC` struct + impl MagrGC
- [x] 2.2 新建 `src/runtime/src/gc/rc_heap_tests.rs` —— RcMagrGC 单元测试（8 个）
- [x] 2.3 新建 `src/runtime/src/gc/README.md` —— 目录 README

## 阶段 3: VmContext 集成
- [x] 3.1 修改 `src/runtime/src/vm_context.rs` —— 加 `heap: Box<dyn MagrGC>` 字段 + `heap()` accessor
- [x] 3.2 修改 `src/runtime/src/vm_context_tests.rs` —— 增加 heap accessor + 隔离测试（3 个）

## 阶段 4: callsite 迁移
- [x] 4.1 `interp/exec_instr.rs:193` —— ArrayNew(size) 走 `ctx.heap().alloc_array`
- [x] 4.2 `interp/exec_instr.rs:199` —— ArrayNewLit 走 `ctx.heap().alloc_array`
- [x] 4.3 `interp/exec_instr.rs:264` —— ObjNew 走 `ctx.heap().alloc_object`
- [x] 4.4 `jit/helpers_object.rs:80` —— jit_array_new 走 `vm_ctx.heap().alloc_array`
- [x] 4.5 `jit/helpers_object.rs:91` —— jit_array_new_lit 走 `vm_ctx.heap().alloc_array`（同时把 `_ctx` 改回 `ctx`）
- [x] 4.6 `jit/helpers_object.rs:208` —— jit_obj_new 走 `vm_ctx.heap().alloc_object`
- [x] 4.7 清理无用 imports：`Rc` / `RefCell` / `ScriptObject`（interp + jit）

## 阶段 5: 文档同步
- [x] 5.1 `docs/design/vm-architecture.md` —— 新增 "GC 子系统 —— MagrGC" 段：trait 形状、Phase 1 RcMagrGC、Phase 路线表、字符串脚本化未来动机、设计权衡
- [x] 5.2 `docs/roadmap.md` —— L2 VM 质量段新增 MagrGC Phase 1 完成条目；A6 backlog 注释 Phase 1 已收口
- [x] 5.3 `gc/README.md` —— 终稿（含核心文件表、入口点、Phase 路线、命名出处）

## 阶段 6: 验证

### 编译状态
- ✅ `cargo build --manifest-path src/runtime/Cargo.toml` —— 0 错误 0 警告
- ✅ `dotnet build src/compiler/z42.slnx` —— 0 错误 0 警告

### 测试结果
- ✅ Rust unit tests: **82/82 通过**（含新增 11 个 GC 测试 + 3 个 VmContext heap 测试）
- ✅ Rust integration tests (`zbc_compat`): **4/4 通过**
- ✅ `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`: **735/735 通过**
- ✅ `./scripts/test-vm.sh`: **interp 101/101 + jit 101/101 = 202/202 通过**

### Spec 覆盖

| Scenario | 实现位置 | 验证方式 | 状态 |
|----------|---------|---------|------|
| alloc_object 创建 ScriptObject | `gc/rc_heap.rs:32` | `rc_heap_tests::alloc_object_returns_value_object_with_given_fields` | ✅ |
| alloc_array 创建带初始元素 | `gc/rc_heap.rs:43` | `rc_heap_tests::alloc_array_returns_value_array_with_given_elems` | ✅ |
| alloc_map 创建空 Map | `gc/rc_heap.rs:48` | `rc_heap_tests::alloc_map_returns_empty_value_map` | ✅ |
| 默认 collect/write_barrier no-op | `gc/heap.rs:53,60` | `heap_tests::default_write_barrier_is_noop` / `default_collect_is_noop` | ✅ |
| VmContext::new 默认 RcMagrGC | `vm_context.rs:74` | `vm_context_tests::heap_is_installed_by_default` | ✅ |
| 多次 alloc 独立 Rc | `gc/rc_heap.rs` | `rc_heap_tests::two_alloc_object_calls_return_distinct_rcs` | ✅ |
| interp ArrayNew 走接口 | `interp/exec_instr.rs:191` | golden tests (`./scripts/test-vm.sh` interp) | ✅ |
| interp ObjNew 走接口 | `interp/exec_instr.rs:262` | golden tests | ✅ |
| JIT helpers 走接口 | `jit/helpers_object.rs:80,91,206` | golden tests (`./scripts/test-vm.sh` jit) | ✅ |
| stats.allocations 单调递增 | `gc/rc_heap.rs:30` | `rc_heap_tests::stats_allocations_monotonically_increases` | ✅ |
| stats.gc_cycles 反映调用 | `gc/rc_heap.rs:55` | `rc_heap_tests::stats_gc_cycles_increments_on_collect_cycles` | ✅ |
| ctx.heap() 返回 trait object | `vm_context.rs:80` | `vm_context_tests::heap_is_installed_by_default` | ✅ |
| 两 ctx heap 隔离 | (instance fields) | `vm_context_tests::two_contexts_heap_isolated` | ✅ |

### Tasks 完成度：6 阶段全部 ✅

### 结论：✅ 全绿，可以归档

## 备注

- Decision 6（Phase 1.5 corelib 迁移延后）已写入 design.md 与 vm-architecture.md
- 文件行数检查：
  - `gc/heap.rs` 76 行 / `gc/rc_heap.rs` 65 行 / `gc/heap_tests.rs` 36 行 / `gc/rc_heap_tests.rs` 102 行
  - `vm_context.rs` 从 171 → 188 行（+17）
  - `interp/exec_instr.rs` 行数减少（删除 5 行 + 移除 import 2 行）
  - `jit/helpers_object.rs` 行数减少（删除 4 行 + 移除 import 4 行）
- 全部新增文件均 < 300 行软限制
