//! Garbage collector —— heap memory management for z42 VM.
//!
//! # 当前状态（Phase 1，2026-04-29 expand-magrgc-mmtk-interface）
//!
//! - [`MagrGC`] —— GC 抽象 trait，对齐 MMTk porting contract（10 个能力组 / ~30 方法）
//! - [`RcMagrGC`] —— 基于 `Rc<RefCell<...>>` 的 RC 后端 + 完整 host-side 嵌入接口
//!   - alloc / roots / write barriers / object model / collection control
//!   - heap config / finalization / weak refs / observers / profiler / stats
//!
//! # Phase 路线（见 `docs/design/vm-architecture.md` "GC 子系统" 段）
//!
//! | Phase | 内容 |
//! |-------|------|
//! | 1（已落地）| trait + RcMagrGC + 全嵌入接口（host-side） |
//! | 1.5（计划）| corelib NativeFn 签名扩展 + 剩余 Rc::new 迁移 |
//! | 2（计划）| 环检测真实实现（Bacon-Rajan / dumpster 2.0） |
//! | 3（计划）| Mark-Sweep + RootScope 真实 trace + write_barrier + GcRef<T> |
//! | 4+（长期）| 分代 / 并发 / MMTk 集成 |

pub mod heap;
pub mod rc_heap;
pub mod types;

pub use heap::MagrGC;
pub use rc_heap::RcMagrGC;
pub use types::{
    AllocKind, AllocSample, AllocSamplerFn, CollectStats, FinalizerFn, FrameMark,
    GcEvent, GcKind, GcObserver, HeapSnapshot, HeapStats, ObjectStats, ObserverId,
    RootHandle, SnapshotCoverage, WeakRef,
};
