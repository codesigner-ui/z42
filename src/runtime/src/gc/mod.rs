//! Garbage collector —— heap memory management for z42 VM.
//!
//! # 当前状态（Phase 1，2026-04-28）
//!
//! - [`MagrGC`] —— GC 抽象 trait，借鉴 MMTk porting contract
//! - [`RcMagrGC`] —— 基于 `Rc<RefCell<...>>` 的引用计数后端
//!   - 行为等价迁移前的直接 `Rc::new(RefCell::new(...))` 构造
//!   - **已知限制**：环引用泄漏（如 `a.next = b; b.next = a`）
//!
//! # Phase 路线（见 `docs/design/vm-architecture.md` "GC 子系统" 段）
//!
//! | Phase | 内容 |
//! |-------|------|
//! | 1（已落地）| trait + RcMagrGC + 6 个脚本驱动 callsite 收口 |
//! | 1.5（计划）| corelib NativeFn 签名扩展 + 剩余 Rc::new 迁移 |
//! | 2（计划）| 环检测真实实现（Bacon-Rajan / dumpster 2.0） |
//! | 3（计划）| Mark-Sweep + RootScope + write_barrier + GcRef<T> |
//! | 4+（长期）| 分代 / 并发 / MMTk 集成 |

pub mod heap;
pub mod rc_heap;

pub use heap::{HeapStats, MagrGC};
pub use rc_heap::RcMagrGC;
