# Tasks: Interp Stack Scanning for Cycle Collector (Phase 3f)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（**bugfix**）

## Bug & 修复

Phase 3d.1 后还遗留场景：脚本执行中调用 `GC.ForceCollect()`，frame 持 outer，
outer 通过 slot 间接持 inner（inner 没在 reg），trial-deletion 把 outer 与
inner 都标 unreachable，从 outer.scan 减 inner 的 tentative → inner=0 → 被
错误清空。

**修复**：interp `exec_function` 入口把当前 `frame.regs` Vec 指针注册到
`VmContext.exec_stack`，external root scanner 闭包遍历喂给 BFS。`FrameGuard`
RAII 保证 push/pop 严格配对（含 panic / `?` early return）。

## 完成清单

### 阶段 1: VmContext.exec_stack ✅
- [x] 加 `exec_stack: Rc<RefCell<Vec<*const Vec<Value>>>>` 字段（注释说明
      raw ptr safety 由 RAII 保证）
- [x] `pub(crate) fn push_frame_regs(&self, ptr: *const Vec<Value>)`
- [x] `pub(crate) fn pop_frame_regs(&self)`

### 阶段 2: scanner 闭包扩展 ✅
- [x] VmContext::new 中 scanner 闭包加第三步：遍历 exec_stack 中每个 raw ptr，
      `unsafe { (*ptr).iter() }` 把 Value 喂给 visit；SAFETY 注释完整

### 阶段 3: interp exec_function RAII guard ✅
- [x] `interp/mod.rs::exec_function` 入口 `ctx.push_frame_regs(&frame.regs as *const _)`
- [x] `struct FrameGuard<'a> { ctx: &'a VmContext }` + `impl Drop` 调用 pop

### 阶段 4: 测试 ✅
- [x] **Rust unit test** `frame_held_outer_with_inner_chain_protected_by_stack_scan`：
      模拟 frame_regs 用 Rc<RefCell> 持 outer，outer.slot → inner；collect 后
      verify inner.slots[0] = 42 未被错误清空
- [x] **z42 golden test** `111_gc_collect_during_exec`：在 Main 中持 outer，
      outer.Child = inner；调 GC.ForceCollect 后输出 `value: 42`，证明
      间接可达对象在脚本执行中触发的 GC 下数据 intact

### 阶段 5: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档：限制 #3 缩到 JIT 栈帧 regs（interp 已对接）
- [x] `gc/README.md` 同步 + 加 Phase 3f 完成说明
- [x] `docs/design/vm-architecture.md` 同步 + Phase 路线表加 3f
- [x] `gc/mod.rs` 加 3f 已落地 + 3f-2 计划

### 阶段 6: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误

**测试结果**：
- ✅ Rust unit tests: **139/139 通过**（+1 Phase 3f 测试）
- ✅ Rust integration tests: **4/4**
- ✅ `dotnet test`: **742/742**
- ✅ `./scripts/test-vm.sh`: **interp 103 + jit 103 = 206/206**（含新增
      111_gc_collect_during_exec）

### 结论：✅ 全绿，可以归档
