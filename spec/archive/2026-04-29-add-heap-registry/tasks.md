# Tasks: Add Heap Registry + Full Snapshot Coverage (Phase 3b)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：refactor + 行为升级（host-side observable）

## 变更说明

`RcMagrGC` 内部新增 **heap registry** —— `Vec<WeakRef>`，每次 `alloc_*` 推入
对应弱引用，让 GC 真正"知道"所有当前存活的堆对象（而不是仅 reachable from
pinned roots）。

收益：
- `iterate_live_objects` 与 `take_snapshot` 升级到 **Full 覆盖**
- `HeapSnapshot.coverage` Phase 3b 起返回 `SnapshotCoverage::Full`
- 是 Phase 3c mark-sweep 算法的物理前置（mark 阶段 candidates 集）

## 完成清单

### 阶段 1: RcHeapInner ✅
- [x] 加 `heap_registry: Vec<WeakRef>` 字段
- [x] Debug impl 加 `registry_size` 字段输出

### 阶段 2: alloc_* 推 WeakRef ✅
- [x] `record_alloc` 签名加 `value: &Value` 参数
- [x] 加内部 helper `make_weak_internal(value)` 复用 trait 逻辑（避开重入借用）
- [x] alloc_object / alloc_array 调用 record_alloc 时把 value 传入

### 阶段 3: iterate / snapshot 走 registry ✅
- [x] 加 `snapshot_live_from_registry()` helper：`borrow_mut` registry
      retain-and-collect，自动 prune 死引用 + visited HashSet 去重
- [x] `iterate_live_objects` 走 registry → Full 覆盖
- [x] `take_snapshot` 走 registry，`coverage = SnapshotCoverage::Full`

### 阶段 4: 自动 prune ✅
- [x] `snapshot_live_from_registry` 内 `retain` 同步 prune（每次 iterate /
      snapshot 顺路把 weak.upgrade() == None 的项移除）

### 阶段 5: 测试 ✅
- [x] 删除旧测试 `snapshot_coverage_is_reachable_from_pinned_roots_in_rc_mode`
      → 改 `snapshot_coverage_is_full_after_phase_3b_registry`，断言 `Full`
- [x] 新增 `snapshot_includes_unpinned_alive_object`
- [x] 新增 `iterate_live_objects_full_coverage_includes_unpinned`
- [x] 新增 `registry_prunes_dropped_objects`
- [x] 现有 reachability 与 cycle dedup 测试自然通过（snapshot_live_from_registry 内置去重）

### 阶段 6: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档 —— 限制清单 #3 移除（Phase 3b 已解决段补充）
- [x] `gc/README.md` —— 限制清单 #3 移除 + 加 Phase 3b 完成注释
- [x] `docs/design/vm-architecture.md` —— Phase 路线表 Phase 3b ✅；已知限制 #3 移除并加完成注释
- [x] `gc/mod.rs` —— Phase 路线表 3b 改"已落地"

### 阶段 7: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **123/123 通过**（含 3 个新 Phase 3b 测试 + 1 个改名测试）
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **740/740**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

### 结论：✅ 全绿，可以归档
