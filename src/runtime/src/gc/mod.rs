//! Garbage collector —— heap memory management for z42 VM.
//!
//! # 当前状态（Phase 1，2026-04-29 expand-magrgc-mmtk-interface）
//!
//! - [`MagrGC`] —— GC 抽象 trait，对齐 MMTk porting contract（10 个能力组 / ~30 方法）
//! - [`GcRef<T>`] / [`WeakGcRef<T>`] —— 堆引用不透明句柄（隐藏 backing 实现）
//! - [`RcMagrGC`] —— Phase 3a 默认后端，`GcRef` 走 `Rc<RefCell<T>>` backing + 完整 host-side 嵌入接口
//!   - alloc / roots / write barriers / object model / collection control
//!   - heap config / finalization / weak refs / observers / profiler / stats
//!
//! # Phase 路线（见 `docs/design/vm-architecture.md` "GC 子系统" 段）
//!
//! | Phase | 内容 |
//! |-------|------|
//! | 1（已落地）| trait + RcMagrGC + 全嵌入接口（host-side） |
//! | 1.5（已落地）| corelib NativeFn 签名带 `&VmContext` + 剩余 Rc::new 迁移 |
//! | 2（**跳过**）| 环检测中间方案（直奔 Phase 3 mark-sweep） |
//! | 3a（已落地）| `GcRef<T>` 不透明句柄抽象（backing 仍 Rc<RefCell<T>>，行为零变化） |
//! | 3b（已落地）| Heap registry + snapshot/iterate Full coverage |
//! | 3c（已落地）| **Trial-deletion 环回收器** —— `collect_cycles` 真实回收环引用 |
//! | 3d（已落地）| Finalizer 真触发（collect 时调度）+ 内存压力自动 collect |
//! | 3d.1（已落地）| External root scanner（VmContext static_fields 自动暴露给 GC，修复漏扫 bug）|
//! | 3d.2（已落地）| `Std.GC.Collect()` / `UsedBytes()` / `ForceCollect()` 暴露给 z42 脚本 + 端到端 golden test 验证 |
//! | 3f（已落地）| interp 栈扫描（FrameGuard RAII 把 frame.regs 暴露给 scanner，修复 transitive bug）|
//! | 3e（已落地）| GcRef backing → `Rc<GcAllocation<T>>` wrapper + Drop 自动触发 finalizer（含纯 Rc Drop 路径）|
//! | 3f-2（计划，可选）| JIT JitFrame.regs 对接为 GC roots（Cranelift stack maps）|
//! | 4+（长期）| 分代 / 并发 / MMTk 集成 |

pub mod heap;
pub mod rc_heap;
pub mod refs;
pub mod types;

pub use heap::MagrGC;
pub use rc_heap::RcMagrGC;
pub use refs::{GcRef, WeakGcRef};
pub use types::{
    AllocKind, AllocSample, AllocSamplerFn, CollectStats, FinalizerFn, FrameMark,
    GcEvent, GcKind, GcObserver, HeapSnapshot, HeapStats, ObjectStats, ObserverId,
    RootHandle, SnapshotCoverage, WeakRef,
};
