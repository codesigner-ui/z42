# Spec: GC Handle + HeapStats + Stdlib Reorganization

## ADDED Requirements

### Requirement: GC subdirectory layout

#### Scenario: GC files colocated under z42.core/src/GC/
- **WHEN** 用户查看 `src/libraries/z42.core/src/GC/`
- **THEN** 看到 5 个文件：`GC.z42`、`GCHandle.z42`、`HeapStats.z42`、
  `WeakHandle.z42`、`README.md`

#### Scenario: 旧路径不再存在
- **WHEN** 检查 `src/libraries/z42.core/src/GC.z42` 与
  `src/libraries/z42.core/src/Delegates/WeakHandle.z42`
- **THEN** 两个旧路径都不存在（已移动到新位置）

#### Scenario: WeakHandle 内部消费者不破
- **WHEN** `src/libraries/z42.core/src/Delegates/SubscriptionRefs.z42` 与
  `DelegateOps.z42` 中调用 `WeakHandle.MakeWeak` / `WeakHandle.Upgrade`
- **THEN** 调用点代码**不变更**（namespace `Std` 内自动可见，物理路径解耦）；
  `dotnet test` 与 `./scripts/test-vm.sh` 全绿

---

### Requirement: GCHandle struct + Strong / Weak 双模

#### Scenario: GCHandle 是 struct，单字段
- **WHEN** 检查 `src/libraries/z42.core/src/GC/GCHandle.z42`
- **THEN** `GCHandle` 是 `public struct` 而非 `public class`；只有一个
  private 字段 `long _slot`

#### Scenario: AllocStrong anchor 目标
- **WHEN** `var target = new SomeClass(); var h = GCHandle.AllocStrong(target);
  target = null; GC.ForceCollect();`
- **THEN** `h.Target` 仍返回原 SomeClass 实例（强引用 anchor，不被回收）

#### Scenario: AllocWeak 不 anchor 目标
- **WHEN** `var target = new SomeClass(); var h = GCHandle.AllocWeak(target);
  target = null; GC.ForceCollect();`
- **THEN** `h.Target` 返回 null（弱引用，目标已被回收）；
  `h.IsAllocated` 仍返回 true（slot 未 Free）

#### Scenario: Free 后失效
- **WHEN** `var h = GCHandle.AllocStrong(target); h.Free();`
- **THEN** `h.IsAllocated` 返回 false；`h.Target` 返回 null

#### Scenario: struct 拷贝共享 backing
- **WHEN** `var h1 = GCHandle.AllocStrong(target); var h2 = h1; h1.Free();`
- **THEN** `h2.IsAllocated` 也返回 false（slot ID 共享，corelib 释放
  影响所有 alias，与 C# `GCHandle` struct 行为一致）

#### Scenario: Kind 反映分配模式
- **WHEN** `var hw = GCHandle.AllocWeak(o); var hs = GCHandle.AllocStrong(o);`
- **THEN** `hw.Kind == GCHandleType.Weak`、`hs.Kind == GCHandleType.Strong`

#### Scenario: 原子值 AllocWeak 返回未分配 handle
- **WHEN** `var h = GCHandle.AllocWeak(42);` （atomic value 不可弱引用）
- **THEN** `h.IsAllocated` 返回 false（_slot=0，slot 未实际占用）

#### Scenario: Alloc(target, type) 工厂支持 enum 路由
- **WHEN** `var h = GCHandle.Alloc(target, GCHandleType.Strong);`
- **THEN** 等价于 `GCHandle.AllocStrong(target)`，行为一致

---

### Requirement: HeapStats + GC.GetStats()

#### Scenario: GetStats 返回当前 HeapStats
- **WHEN** `var s = GC.GetStats();`
- **THEN** s 是 `HeapStats` 实例；`s.UsedBytes`、`s.Allocations`、
  `s.GcCycles` 都是非负 long；分配新对象后调用，`s.UsedBytes` 单调递增

#### Scenario: MaxBytes unlimited 用 -1
- **WHEN** 未调用 `set_max_heap_bytes`（默认无限制），`var s = GC.GetStats();`
- **THEN** `s.MaxBytes == -1`（z42 无 Optional<T>，sentinel 表示 unlimited）

#### Scenario: HeapStats 7 字段全暴露
- **WHEN** 检查 `src/libraries/z42.core/src/GC/HeapStats.z42` 的 public 属性
- **THEN** 包含且仅包含：Allocations / GcCycles / UsedBytes / MaxBytes /
  RootsPinned / FinalizersPending / Observers（全部 long getter）

---

### Requirement: WeakHandle 保留作为低级 primitive

#### Scenario: WeakHandle.MakeWeak / Upgrade 行为不变
- **WHEN** `var w = WeakHandle.MakeWeak(target);`
- **THEN** 行为与本变更前一致；唯一差别是文件路径从
  `src/libraries/z42.core/src/Delegates/WeakHandle.z42` 移到
  `src/libraries/z42.core/src/GC/WeakHandle.z42`

#### Scenario: GCHandle 与 WeakHandle 是独立 primitive
- **WHEN** 同时使用 GCHandle 和 WeakHandle
- **THEN** GCHandle 走 corelib HandleTable slot；WeakHandle 走 corelib
  既有 `Weak<RefCell<Object>>` 直接持有；两者互不依赖、互不替代

## IR Mapping

不引入新 IR opcode；GCHandle struct 走既有 struct value-copy 路径；6 个新
builtin 通过既有 `[Native("__gc_handle_*")]` + `[Native("__gc_stats")]`
attribute 走 `BuiltinInstr` dispatch（与 `__gc_collect` / `__obj_make_weak`
等已有 builtin 同 pattern）。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [ ] TypeChecker — 不涉及（struct + extern + static + enum 已支持）
- [x] **stdlib script —— GCHandle struct + GCHandleType enum + HeapStats class**
- [x] **Rust corelib —— HandleTable trait + slab impl + 6 builtins**
- [ ] IR Codegen — 不涉及
- [x] **VM interp —— 通过 dispatch_table 路由 6 个新 builtin**

## Documentation

- `docs/design/stdlib.md` — z42.core 目录树更新；加 GCHandle 章节简介
- `docs/design/gc-handle.md` — **NEW** 实现原理文档（HandleTable 数据结构、
  Strong/Weak slot 语义、struct 共享 backing 模式、Phase 3 Pinned 扩展规划）
- `docs/roadmap.md` — 加本变更条目
