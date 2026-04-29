# Tasks: Drop-Time Finalizer Triggering (Phase 3e)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（行为升级）

## 完成清单

### 阶段 1: GcAllocation wrapper ✅
- [x] `gc/refs.rs::GcAllocation<T> { inner: RefCell<T>, finalizer: RefCell<Option<FinalizerFn>> }`
- [x] `impl<T> Drop for GcAllocation<T>`：take + 调用 finalizer（one-shot）
- [x] `GcRef<T>` backing 改为 `Rc<GcAllocation<T>>`
- [x] 所有 GcRef 方法（new / borrow / borrow_mut / ptr_eq / as_ptr / downgrade
      / Clone / Debug / strong_count）适配
- [x] 加 pub(crate) 方法：`set_finalizer` / `cancel_finalizer` / `has_finalizer`
- [x] `WeakGcRef<T>` 同步 wrap `Weak<GcAllocation<T>>`
- [x] `as_ptr` 返回 GcAllocation 内 inner 字段地址（保持身份哈希语义）

### 阶段 2: RcMagrGC 简化 ✅
- [x] 移除 `finalizers: HashMap<usize, FinalizerFn>` 字段
- [x] `register_finalizer` 改走 `GcRef::set_finalizer`
- [x] `cancel_finalizer` 改走 `GcRef::cancel_finalizer`
- [x] `run_cycle_collection` 移除显式 finalizer dispatch，注释说明 Drop 链
      自动触发（含 cycle 断环后 alive_vec drop）
- [x] `stats()` 即时遍历 heap_registry 重算 `finalizers_pending`
- [x] Debug impl 移除 finalizers_count

### 阶段 3: 测试 ✅（3 个新测试）
- [x] `finalizer_fires_on_normal_rc_drop_no_cycle_no_collect` —— 关键：alloc +
      register + drop 最后一个引用 → 自动触发，**无需 collect_cycles**
- [x] `finalizer_one_shot_via_drop_then_collect` —— Drop 后再 collect 不重发
- [x] `finalizers_pending_reflects_alive_with_finalizer` —— stats 重算正确
- [x] 现有 finalizer 测试（cycle collect 路径 + does_not_fire + one_shot_after_fire）
      自然过：collect 仍触发 finalizer，只是路径从"显式 dispatch"变成"alive_vec
      drop 链 → GcAllocation::Drop"

### 阶段 4: 文档同步 ✅
- [x] `gc/refs.rs` 模块文档 + GcAllocation 说明 + Phase 3e drop 钩子
- [x] `gc/rc_heap.rs` 模块文档：限制清单从 3 项 → 2 项（finalizer 限制移除）
- [x] `gc/README.md` 同步
- [x] `docs/design/vm-architecture.md` 同步 Phase 3e ✅
- [x] `gc/mod.rs` Phase 路线表 3e ✅

### 阶段 5: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误

**测试结果**：
- ✅ Rust unit tests: **142/142 通过**（+3 Phase 3e 测试）
- ✅ Rust integration tests: **4/4**
- ✅ `dotnet test`: **742/742**
- ✅ `./scripts/test-vm.sh`: **interp 103 + jit 103 = 206/206**

### 结论：✅ 全绿，可以归档

## 后续

剩余限制：
1. OOM 真拒绝（trait API 升级，独立 spec）
2. JIT JitFrame.regs 对接（Phase 3f-2，视需要）

GC 子系统主功能至此完整：alloc / dealloc / cycle collect / finalizer（含
drop-time）/ 自动 collect / observers / profiler / stats。可投产。
