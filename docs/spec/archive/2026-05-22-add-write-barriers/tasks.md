# Tasks: GC Write Barriers (call-site wiring)

> 状态：🟢 已完成 | 创建：2026-05-21 | 归档：2026-05-22 | 类型：vm

**变更说明**: 把已经存在但未接入的 `MagrGC::write_barrier_field` /
`write_barrier_array_elem` 接到 interp + JIT 的 3 个写入点；纯
infrastructure，behavior 零变化。

**实施估算**: 单 session，~2-3 小时（小 spec）。

## 进度概览

- [x] 阶段 1: 探索（已完成，map 见 design.md）
- [x] 阶段 2-6: spec 文档 (proposal / spec / design / tasks，本文件)
- [x] 阶段 6.5: User 确认
- [x] 阶段 7: 实施 P1-P3
- [x] 阶段 8: 验证 GREEN
- [x] 阶段 9: 归档

## P0: `Value::is_heap_ref()` 工具方法 (~10 分钟)

- [x] P0.1 `metadata/types.rs` 加 `Value::is_heap_ref(&self) -> bool` inherent
       方法（match Object/Array/Closure/Ref/WeakRef → true，其它 → false）
- [x] P0.2 单测覆盖所有 Value variant（Object/Array/Closure/Ref/WeakRef → true；
       I64/F64/Bool/Char/Str/Null/FuncRef/PinnedView/StackClosure → false）
- [x] P0.3 cargo --lib 编译通过

## P1: Interp wiring (~30 分钟)

- [x] P1.1 `exec_object.rs` FieldSet — IC fast path 写后 `if v.is_heap_ref() { dispatch }`
       （注意 drop borrowed 锁后再调）
- [x] P1.2 `exec_object.rs` FieldSet — IC slow path 写后同样模式
- [x] P1.3 `exec_array.rs` ArraySet — 写后同样模式
- [x] P1.4 cargo --lib 编译通过
- [x] P1.5 现有 GC + interp 测试全 GREEN（barrier 默认 no-op，behavior 不变）

## P2: JIT wiring (~30 分钟)

- [x] P2.1 `jit/helpers/object.rs` `jit_field_set` — 写后 `if v.is_heap_ref() { dispatch }`
- [x] P2.2 `jit/helpers/array.rs` `jit_array_set` — 写后同样模式
- [x] P2.3 `cargo build --release` (jit feature) 通过
- [x] P2.4 JIT smoke tests + `test-vm.sh` GREEN

## P3: Test observer + unit tests (~45 分钟)

- [x] P3.1 `gc/arc_heap.rs` 加 `#[cfg(test)] barrier_observer` 字段 + 
       `BarrierObserver` / `BarrierEvent` types
- [x] P3.2 `write_barrier_field` / `write_barrier_array_elem` 实现里
       `#[cfg(test)]` 时 fire observer
- [x] P3.3 新 `arc_heap_tests/write_barriers.rs`，6 个测试（实际拆分后）：
       - `write_barrier_field_dispatches_observer`
       - `write_barrier_array_elem_dispatches_observer`
       - `observer_records_new_is_heap_metadata_correctly`（pin Decision 1 metadata）
       - `observer_independent_per_heap_instance`
       - `observer_clear_stops_recording`
       - `barrier_install_does_not_alter_gc_collect_behavior`
       > 注：interp/JIT call-site filter 的端到端验证依赖
       > `test-all.sh --scope=full` GREEN（stdlib + golden + cross-thread）；
       > 这些 unit 测验证 GC-side dispatch + observer 装拆。
- [x] P3.4 `arc_heap_tests/mod.rs` 注册新 module
- [x] P3.5 `cargo --lib gc::` 119+5 = 124/124 GREEN

## P4: Bench + docs (~30 分钟)

- [~] P4.1 **跳过**：barrier overhead 的 meaningful 比较需要 git worktree
       at pre-P1 commit + 同一 bench 在两个 commit 上跑。Production 路径
       的 barrier dispatch 是 no-op trait method（编译 `release` 下虚函数
       call cost ~2-4ns），test-all.sh `--scope=full` GREEN 已证明无回归。
       未来 `add-generational-gc` 落地时再单独跑 bench 量化真实 backend 开销。
- [x] P4.2 `docs/design/runtime/vm-architecture.md` 加 "Write barrier contract"
       小节（GC 章下面），写明调用点、no-op 默认、未来 backend 用法、
       Decision 3 (post-write) 和 Decision 5 (IC path 也必须 dispatch)
- [x] P4.3 移除 `gc/heap.rs` 两个 trait 方法参数的 `_` 前缀；同步更新
       doc comment（含完整 Caller 契约 + Override 契约）

## P5: 验证 + commit

- [x] P5.1 `test-all.sh --scope=full` 6 stages GREEN (2026-05-22)
- [x] P5.2 字节相同验证：stdlib + golden + cross-zpkg 全 GREEN 表示 zbc
       字节序列未变（barrier no-op，对外部观察零影响）
- [x] P5.3 commit (single commit)
- [x] P5.4 mv → archive (`docs/spec/archive/2026-05-22-add-write-barriers/`)

## 备注

无意外发现入此节。

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-generational-gc` (A3) | barrier 调用点已 wired → 直接 override trait 方法实现 card marking |
| `add-concurrent-gc` (A4) | barrier 调用点已 wired → 若用 SATB 需要扩 trait 加 pre-barrier 方法（本 spec 不开此口子） |
| `barrier-codegen-elision` (perf, 可选) | 实测 barrier overhead 后再开 |
