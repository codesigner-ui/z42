# Proposal: Mark-sweep GC (replace trial-deletion)

## Why

`docs/design/runtime/vm-architecture.md` "GC 后续迭代规划" A2 标记 mark-sweep
为长期方向，依据：

- **算法复杂度**：当前 trial-deletion 在 cycle 多的 workload 下是 O(N²) —
  扫所有 unreachable + 减计算每个对象的"外部强引用数"
- **纯 tracing 复杂度**：O(reachable) — 只走可达对象，不可达对象通过 sweep 阶段直接扫掉
- **行为简化**：去掉 RC + 环检测混合模型；统一为 mark phase + sweep phase
- **未来 spec 解锁**：`add-concurrent-gc` (A4) 必须在 tracing GC 上做（Rc
  内部计数器跨线程开销 + trial-deletion 难并行化）

当前 `ArcMagrGC::run_cycle_collection` 走 Bacon-Rajan 简化版 trial-deletion，
代码 ~150 行 + 6 步骤逻辑。替换为标准 mark-sweep 后预计减到 ~80 行 + 2 步骤。

**Spec 文档先行（add-spec-only-mode, 2026-05-21）**：本 spec 落地的是
proposal + design + tasks 文档；实施延后到专门 session（multi-day 工作量
不适合 spec-and-go 节奏）。User 批准 spec 后，实施 phase 开新 session
专注完成 + benchmark + 全套 GC 单测迁移。

## What Changes

替换 `ArcMagrGC` 的 cycle collector 路径：

- **删除**：`run_cycle_collection` (trial-deletion) + 辅助 `tentative` /
  `break_cycle_value` 逻辑 + Rc strong_count 依赖
- **新增**：`mark_phase` (BFS from roots) + `sweep_phase` (registry 扫死对象)
- **GcAllocation 加 mark bit**：`AtomicU8` (0/1)，mark phase 写 1，sweep
  phase 检查 0 即扫
- **Trace trait**：所有 Value variant 实现 `trace_children(&dyn FnMut(&Value))`
  让 mark phase 递归走
- **drop 语义变化**：Rc 即时释放路径保留（最后一个引用 drop 时仍释放），
  但 cycle break 不再由 trial-deletion 处理 —— 完全靠 sweep
- **GC 单测迁移**：~40 个 `arc_heap_tests/` 单测可能需要更新 assertion
  （e.g., "环回收前 used_bytes" → "mark-sweep 后 used_bytes" 时序不同）

## Scope（允许改动的文件 — implementation phase）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/refs.rs` | MODIFY | `GcAllocation<T>` 加 `marked: AtomicU8` 字段 |
| `src/runtime/src/gc/types.rs` | MODIFY | 可能加 `Trace` trait（如已有评估复用） |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | run_cycle_collection 重写：mark BFS + sweep；删除 trial-deletion |
| `src/runtime/src/gc/arc_heap_tests/cycle_collection.rs` | MODIFY | assertion 调整 |
| `src/runtime/src/gc/arc_heap_tests/finalization.rs` | MODIFY | drop 时序若变化，迁移 |
| `src/runtime/src/gc/arc_heap_tests/roots.rs` | MODIFY | mark from roots 测试 |
| `src/runtime/src/gc/arc_heap_tests/collection.rs` | MODIFY | gc_cycles / collect_cycles 行为校准 |
| `src/runtime/src/gc/arc_heap_tests/events.rs` | MODIFY | BeforeCollect/AfterCollect 事件时序 |
| `src/runtime/src/gc/arc_heap_tests/config_stats.rs` | MODIFY | 同上 |
| `src/runtime/src/metadata/types.rs` | MODIFY | Value 加 trace_children 方法（如选 trait 而非 inherent） |
| `docs/design/runtime/vm-architecture.md` | MODIFY | A2 mark-sweep 段从"未来计划"改为"已落地"；trial-deletion 移到 historical 章 |
| `docs/spec/changes/add-mark-sweep-collector/` | NEW | 本 spec（spec-only 阶段） |

**只读引用**：

- `docs/design/runtime/vm-architecture.md` A2 条目
- `src/runtime/src/gc/arc_heap.rs` 现 run_cycle_collection 实现（~150 行）
- 现有 arc_heap_tests/ ~40 个测试用例了解 fixture pattern

## Out of Scope

- **A1 自定义 allocator**：vm-architecture.md A2 名义上 deps A1，但实际 A2 可以在
  Arc backing 上直接做（mark bit 加 Arc 内部）；A1 是独立优化轨道
- **A3 generational**：A2 落地后再考虑分代；write barrier 在 A3 引入
- **A4 concurrent collector**：本 spec 的后继；A2 落地后 spec
- **Drop 语义大改**：保留 Arc 即时 drop（防 user expecting "ref count to 0 = immediate drop"
  代码崩）；mark-sweep 只接管 cycle breaking 路径

## Open Questions

- [ ] **mark bit 放 Arc 内还是单独表**：Arc 内 (`GcAllocation.marked`) cache-friendly
      但增大对象 size；单独 HashMap<*const, bool> 灵活但内存额外。Design Decision 1
- [ ] **Trace trait 还是 inherent method**：trait 灵活（可被 user 类型实现）但
      object-safety 复杂；inherent 简单。Design Decision 2
- [ ] **collect_cycles 阶段：stop-the-world (current) 还是 incremental**：v0 仍
      STW；incremental 是 A4。Design Decision 3
- [ ] **现有 tests 多少需要调整**：trial-deletion 的 freed_bytes 计算与 mark-sweep
      不一致；需要 baseline run + 逐个 fixture 迁移。Design Decision 4
