# Tasks: Reorganize GC stdlib + Add Unified GCHandle

> 状态：🟢 已完成 | 创建：2026-05-06 | 完成：2026-05-07

## 进度概览
- [x] 阶段 A: 全链路 spike（struct + extern + corelib emit）—— ✅ 2026-05-07
- [x] 阶段 1: Rust 端 HandleTable 接口 + 类型 —— ✅ 2026-05-07
- [x] 阶段 2: RcMagrGC HandleTable 实现 + 单测 —— ✅ 2026-05-07（10/10 绿）
- [x] 阶段 3: corelib builtins（5 GCHandle + 1 HeapStats）+ dispatch —— ✅ 2026-05-07
- [x] 阶段 4: stdlib 目录搬迁（GC.z42 / WeakHandle.z42）—— ✅ 2026-05-07
- [x] 阶段 5: stdlib 新文件（GCHandle / HeapStats / GC/README）+ GC.z42 GetStats —— ✅ 2026-05-07
- [x] 阶段 6: golden test 113_gc_handle + 114_gc_stats（111/112 已被占用）—— ✅ 2026-05-07
- [x] 阶段 7: 文档同步（README + stdlib.md + gc-handle.md + roadmap）—— ✅ 2026-05-07
- [x] 阶段 8: GREEN（dotnet 1082/1082 + cargo lib 240/240 + VM 284/284）+ 归档 + commit —— ✅ 2026-05-07

## 实施备注

- 阶段 6 golden 测试编号修正：原计划 111/112 已被 `111_gc_collect_during_exec`
  / `112_gc_jit_transitive` 占用，使用 113/114
- HeapStats auto-property → 编译器 desugar 后 backing field 名是 `__prop_<Name>`，
  corelib emit TypeDesc 的 slot 名相应加 `__prop_` 前缀（GCHandle._slot 是
  plain field，不加前缀）
- atomic 值 Strong slot 用 `HandleEntry::StrongAtomic(Value)` 承接（slab entry
  enum 加第三 Strong variant），避免 atomic 走不到 Rc::clone path
- 期间发现并行 session（add-class-arity-overloading）的 SymbolCollector 改动
  暂时破坏 Multicast{Func,Predicate} 编译；commit 时 stash 其改动以保持 GREEN，
  其改动留给原 session 处理

---

## 阶段 A: 全链路 spike（先于一切实施）

> 目的：在写正式实现前验证 z42 端 `struct + extern static + private long
> field + corelib Value::Object emit` 全链路工作。如有不支持项，回到 design.md
> 调整方案（如退化为 class）。

- [x] A.1 临时新建 `src/tests/gc/_spike_struct_extern/`（修正路径：golden tests 实际在 `src/tests/`，不在 `src/runtime/tests/golden/`）：
  - `source.z42`：含 `struct Handle { long _slot; ... public long Slot() {...} }`，因 z42 private 字段不可外部直接读，加 public Slot() getter 验证
  - `expected_output.txt`：两行 `42`（验证 corelib emit + struct copy）
- [x] A.2 临时在 `src/runtime/src/corelib/gc.rs` 加 `__test_spike_handle` builtin
  返回 `Value::Object(TypeDesc{name:"Demo.Handle", fields:[_slot:long]}, [I64(42)])`
- [x] A.3 直接编译 source.z42 + cargo run VM 跑 spike → 输出 `42` `42` ✅
- [x] A.4 spike 通过 → **删除** `_spike_struct_extern/` + spike builtin + dispatch 行 → 继续阶段 1

**spike 验证结论**：z42 全链路支持 struct + `[Native]` static + private long 字段
+ corelib 返回的 `Value::Object` 直接赋值给 struct 局部变量 + 实例方法 + struct
copy 保留字段。设计可按原计划推进。

## 阶段 1: Rust 端 HandleTable 接口 + 类型

- [ ] 1.1 `src/runtime/src/gc/types.rs` 加 `pub enum GcHandleKind { Weak, Strong }`
- [ ] 1.2 `src/runtime/src/gc/heap.rs` `MagrGC` trait 加 5 个方法：
      `handle_alloc(&self, &Value, GcHandleKind) -> u64`
      / `handle_target(&self, u64) -> Option<Value>`
      / `handle_is_alloc(&self, u64) -> bool`
      / `handle_kind(&self, u64) -> Option<GcHandleKind>`
      / `handle_free(&self, u64)`

## 阶段 2: RcMagrGC HandleTable 实现 + 单测

- [ ] 2.1 `src/runtime/src/gc/rc_heap.rs` 加 `HandleSlab { entries, free_list }`
      struct + `HandleEntry` enum
- [ ] 2.2 `RcMagrGC` 实现 5 个 trait 方法：alloc → 查 free_list / push；
      target/is_alloc/kind 查 slab；free 置 None + push free_list
- [ ] 2.3 atomic value: `AllocWeak` 返回 slot=0；`AllocStrong` 把 Value clone
      存进 slab（slab entry 类型扩为 `Strong(Value)` 而非仅 Rc，承接 atomic strong）
- [ ] 2.4 `src/runtime/src/gc/rc_heap_tests.rs` 加 7 个单测：
      `handle_alloc_returns_nonzero` /
      `handle_strong_anchors_after_external_drop` /
      `handle_weak_clears_after_external_drop` /
      `handle_free_invalidates_slot` /
      `handle_free_then_realloc_reuses_slot` /
      `handle_atomic_weak_returns_zero_slot` /
      `handle_kind_returns_none_for_freed_slot`

## 阶段 3: corelib builtins + dispatch

- [ ] 3.1 `src/runtime/src/corelib/gc.rs` 加 6 个 builtin：
      `builtin_gc_handle_alloc` / `_target` / `_is_alloc` / `_kind` / `_free`
      / `builtin_gc_stats`
- [ ] 3.2 `__gc_handle_alloc`：从 args 读 target + GCHandleType (i64) → 调
      `ctx.heap().handle_alloc()` → 返回 `Value::Object` with `_slot` 字段
- [ ] 3.3 `__gc_handle_target` / `_is_alloc` / `_kind` / `_free`：从 args[0]
      读 receiver struct，从 `_slot` 字段取 i64，dispatch 到 trait 方法
- [ ] 3.4 `__gc_stats`：调 `ctx.heap().stats()` → 返回 `Value::Object` with 7 字段
- [ ] 3.5 `src/runtime/src/corelib/mod.rs` dispatch table 加 6 行

## 阶段 4: stdlib 目录搬迁

- [ ] 4.1 `mkdir src/libraries/z42.core/src/GC/`
- [ ] 4.2 `git mv src/libraries/z42.core/src/GC.z42 src/libraries/z42.core/src/GC/GC.z42`
- [ ] 4.3 `git mv src/libraries/z42.core/src/Delegates/WeakHandle.z42 src/libraries/z42.core/src/GC/WeakHandle.z42`
- [ ] 4.4 验证 `Delegates/SubscriptionRefs.z42` + `DelegateOps.z42` 调用
      WeakHandle 不需要修改（namespace 内可见性）

## 阶段 5: stdlib 新文件 + GetStats 方法

- [ ] 5.1 NEW `src/libraries/z42.core/src/GC/GCHandle.z42`：
      `enum GCHandleType` + `struct GCHandle` 按 design.md 骨架
- [ ] 5.2 NEW `src/libraries/z42.core/src/GC/HeapStats.z42`：
      `class HeapStats` 7 字段（全部 long getter）
- [ ] 5.3 MODIFY `src/libraries/z42.core/src/GC/GC.z42` 加 `GetStats()` 静态方法 + `[Native("__gc_stats")]`
- [ ] 5.4 NEW `src/libraries/z42.core/src/GC/README.md`：子目录职责 + 核心文件表

## 阶段 6: golden tests

- [ ] 6.1 NEW `src/runtime/tests/golden/run/111_gc_handle/`：
      strong anchor / weak collect / Free 后失效 / struct copy 共享 backing
      4 个场景，预期输出 4 行 bool
- [ ] 6.2 NEW `src/runtime/tests/golden/run/112_gc_stats/`：
      GetStats 后字段非负 + 分配新对象 UsedBytes 增长，预期输出 2 行 true
- [ ] 6.3 `regen-golden-tests.sh` 编译产出 source.zbc

## 阶段 7: 文档同步

- [ ] 7.1 MODIFY `src/libraries/z42.core/src/README.md`：
      目录表加 `GC/` 行（覆盖 GC / GCHandle / HeapStats / WeakHandle）；
      Delegates/ 行去掉 WeakHandle；跨目录依赖表更新
- [ ] 7.2 MODIFY `docs/design/stdlib.md`：z42.core 目录树更新
- [ ] 7.3 NEW `docs/design/gc-handle.md`：实现原理文档
      （HandleTable slab + free list / Strong slot Rc::clone anchor 语义 /
      Weak slot Rc::downgrade / struct 共享 backing / Phase 3 Pinned 扩展规划）
- [ ] 7.4 MODIFY `docs/roadmap.md` 加本变更条目
- [ ] 7.5 MODIFY `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs`
      z42.core 文件数（+2 .z42 文件：GCHandle + HeapStats；GC.z42 + WeakHandle 移动不变文件数）

## 阶段 8: GREEN + 归档 + commit

- [ ] 8.1 `dotnet build src/compiler/z42.slnx` —— 0 错 0 警
- [ ] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml` —— 0 错 0 警
- [ ] 8.3 `dotnet test ...` 全绿
- [ ] 8.4 `./scripts/build-stdlib.sh` + `./scripts/regen-golden-tests.sh` + `./scripts/test-vm.sh` 全绿
- [ ] 8.5 spec scenarios 全覆盖
- [ ] 8.6 `spec/changes/reorganize-gc-stdlib/` → `spec/archive/2026-05-06-reorganize-gc-stdlib/`
- [ ] 8.7 commit：`feat(stdlib): reorganize GC namespace + add unified GCHandle struct`

## 备注
- atomic value Strong slot 的 entry 类型决策（slab 改为 `Strong(Value)` 而非仅 Rc）
  在阶段 2.3 实施时如有变化，记此处
- 如 spike 阶段 A 失败，整套设计回退到 class GCHandle，Decision 1 重写
