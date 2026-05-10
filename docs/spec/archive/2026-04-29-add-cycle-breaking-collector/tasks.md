# Tasks: Add Trial-Deletion Cycle Collector (Phase 3c)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（行为升级：cycle leak 修复）

## 变更说明

`RcMagrGC::collect_cycles()` 与 `force_collect()` 从 no-op stub 升级为**真实环
引用回收器**：基于 Bacon-Rajan trial-deletion 思想，**保留 RC backing**，通过
heap_registry + scan_object_refs + `Rc::strong_count` 检测纯环内对象，主动
清空内部引用断环让 Rc 自然 Drop 释放。

## 完成清单

### 阶段 1: GcRef::strong_count ✅
- [x] `gc/refs.rs::GcRef::strong_count(this: &Self) -> usize`（pub(crate)，
      暴露 Rc::strong_count 给环回收器；注释说明 Phase 3e+ 可能换实现）

### 阶段 2: collect_cycles 实现 ✅
- [x] `collect_cycles` 调用 `run_cycle_collection()`，触发 BeforeCollect /
      AfterCollect 事件，更新 `gc_cycles` 与 `used_bytes`
- [x] `force_collect` 同样升级，返回 `CollectStats { freed_bytes, pause_us, kind: Some(Full) }`
- [x] pause_count > 0 跳过

### 阶段 3: 算法 helpers ✅
- [x] `mark_reachable_set()` —— BFS from pinned roots，返回 reachable HashSet
- [x] `break_cycle_value(v)` —— Object slots → Null；Array vec.clear()
- [x] `run_cycle_collection()` —— mark + filter + trial deletion + break，
      返回估算 freed_bytes

### 阶段 4: 测试 ✅（7 个新测试）
- [x] `simple_two_node_cycle_is_freed_after_collect` —— a-b 互引用，drop 后
      collect 让两者一并释放
- [x] `self_reference_cycle_is_freed` —— a.slots[0] = a，self-cycle 释放
- [x] `cycle_with_external_user_ref_is_not_broken_yet` —— 用户外部持 a，
      collect 不破坏 a；用户后 drop 时 RC drop 链自然完成
- [x] `pinned_root_cycle_is_not_broken` —— pin_root(a)，collect 不动环
- [x] `unrelated_alive_object_is_not_affected_by_collect` —— 非环对象数据 intact
- [x] `multiple_disjoint_cycles_all_freed` —— 两个独立环各自释放
- [x] `collect_cycles_freed_bytes_observable` —— force_collect 返回 freed > 0，
      `used_bytes` 减少

### 阶段 5: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档：限制清单从 4 项改 3 项（环泄漏 + used_bytes
      已解决），加 Phase 3c 完成段
- [x] `gc/README.md` 同步
- [x] `docs/design/vm-architecture.md` 同步（含 Phase 路线表 3c ✅、3e/3f 重排）
- [x] `gc/mod.rs` Phase 路线表 3c ✅
- [x] `docs/roadmap.md` MagrGC 子系统行更新（含 3a/3b/3c 全部完成）

### 阶段 6: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **130/130 通过**（含 7 个新 cycle 测试）
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **740/740**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

### 结论：✅ 全绿，可以归档

## 后续

- Phase 3d：Finalizer 真触发（在 break_cycle 时调度）+ OOM 拒绝
- Phase 3e（可选）：替换 GcRef backing 为自定义堆 + 真 mark-sweep（性能 / generational 准备）
- Phase 3f：Cranelift stack maps（让 collect 在 interp/JIT 执行中也能安全调用）
