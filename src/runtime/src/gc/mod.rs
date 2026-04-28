//! Garbage collector — memory management for heap-allocated z42 objects.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality: this module contains no implementation. Heap-allocated
//! objects (`ScriptObject`, strings, arrays) are kept alive by `Rc<RefCell<T>>`
//! reference counting throughout the interpreter (see `metadata::types::Value`).
//!
//! **Limitation**: `Rc` does not collect cycles. User code like
//! `class Node { Node next; }` with `a.next = b; b.next = a;` will leak
//! both nodes. This is a known unsoundness for Phase 1; documented here so
//! readers don't assume tracing GC semantics.
//!
//! **Future work** (no milestone bound; targets L3+ once self-hosting is
//! reachable): replace `Rc` with a proper tracing GC, restoring cycle
//! collection and laying groundwork for multi-threaded execution.
//! See `docs/roadmap.md` ("固定决策" 段：z42 始终带 GC) and review2 §5.2.
