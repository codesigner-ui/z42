//! Supporting types for the [`MagrGC`](super::heap::MagrGC) trait.
//!
//! Houses every data type that the GC interface contracts on (handles, events,
//! observer trait, profiler types, snapshots, …) so that `heap.rs` can stay
//! focused on the trait shape itself.
//!
//! Threading bounds: every callback type (`GcObserver`, `AllocSamplerFn`,
//! `FinalizerFn`) carries `Send + Sync`. The host is expected to wire these
//! into multi-threaded telemetry / metrics pipelines, so the bound is required
//! even though `VmContext` itself is currently single-threaded.

use std::collections::HashMap;
use std::sync::Arc;

use super::refs::WeakGcRef;
use crate::metadata::{ScriptObject, Value};

// ── Handles ──────────────────────────────────────────────────────────────────

/// Opaque handle returned by [`MagrGC::pin_root`](super::heap::MagrGC::pin_root);
/// pass to [`MagrGC::unpin_root`](super::heap::MagrGC::unpin_root) to release.
///
/// Modeled after V8's `Persistent<T>::Reset()` and .NET's `GCHandle.Free()` —
/// host has explicit ownership semantics rather than RAII.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RootHandle(pub u64);

/// Frame scope marker: the depth of the pin-stack at the moment of
/// [`MagrGC::enter_frame`](super::heap::MagrGC::enter_frame). Pass to
/// [`MagrGC::leave_frame`](super::heap::MagrGC::leave_frame) to drop every
/// pin added inside that frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameMark(pub u32);

/// Observer identity returned by
/// [`MagrGC::add_observer`](super::heap::MagrGC::add_observer); used with
/// [`MagrGC::remove_observer`](super::heap::MagrGC::remove_observer).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ObserverId(pub u64);

// ── GC events / observer ─────────────────────────────────────────────────────

/// GC kind discriminator carried in [`GcEvent`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcKind {
    /// Generational minor collection (Phase 4+).
    Minor,
    /// Stop-the-world full collection (Phase 3+).
    Full,
    /// Reference cycle collector (Phase 2+).
    CycleCollector,
}

/// Lifecycle / pressure events emitted by the heap to registered observers.
#[derive(Debug, Clone)]
pub enum GcEvent {
    /// Fired immediately before a collection starts.
    BeforeCollect      { kind: GcKind, used_bytes: u64 },
    /// Fired after a collection completes.
    AfterCollect       { kind: GcKind, freed_bytes: u64, pause_us: u64 },
    /// Allocation happening with heap usage at >75% of `max_bytes`.
    AllocationPressure { used_bytes: u64, limit_bytes: u64 },
    /// Heap usage crossed >90% of `max_bytes` (deduped per RcMagrGC instance).
    NearHeapLimit      { used_bytes: u64, limit_bytes: u64 },
    /// Allocation would exceed `max_bytes`; Phase 1 still allows the allocation
    /// (RC mode does not enforce). Phase 3 tracing GC may reject.
    OutOfMemory        { requested_bytes: u64, limit_bytes: u64 },
}

/// Embedding-side event subscriber.
///
/// `Send + Sync` so observers can sit in a host metrics / telemetry pipeline
/// that crosses threads (e.g. forward events to an async runtime).
pub trait GcObserver: std::fmt::Debug + Send + Sync {
    fn on_event(&self, event: &GcEvent);
}

// ── Allocation sampling ──────────────────────────────────────────────────────

/// Allocation kind for [`AllocSample`].
#[derive(Debug, Clone)]
pub enum AllocKind {
    Object { class: String },
    Array  { elem_count: usize },
}

/// One allocation sample delivered to an installed
/// [`AllocSamplerFn`].
#[derive(Debug, Clone)]
pub struct AllocSample {
    pub kind:         AllocKind,
    pub size_bytes:   usize,
    /// Microseconds since `RcMagrGC::EPOCH` (process start, monotonic).
    pub timestamp_us: u64,
}

/// Allocation sampler callback installed via
/// [`MagrGC::set_alloc_sampler`](super::heap::MagrGC::set_alloc_sampler).
pub type AllocSamplerFn = Arc<dyn Fn(&AllocSample) + Send + Sync>;

// ── Finalization ─────────────────────────────────────────────────────────────

/// Finalizer callback registered via
/// [`MagrGC::register_finalizer`](super::heap::MagrGC::register_finalizer).
///
/// **Phase 1 RC mode**: registration is recorded but the callback is never
/// invoked (RC has no Drop hook for arbitrary objects). Phase 3 mark-sweep
/// schedules finalizers post-collection.
pub type FinalizerFn = Arc<dyn Fn() + Send + Sync>;

// ── Collection result ────────────────────────────────────────────────────────

/// Result of [`MagrGC::force_collect`](super::heap::MagrGC::force_collect).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CollectStats {
    pub freed_bytes: u64,
    pub pause_us:    u64,
    /// `None` when collection was skipped (e.g. heap is paused).
    pub kind:        Option<GcKind>,
}

// ── Weak references ──────────────────────────────────────────────────────────

/// Weak reference to a heap value.
///
/// Constructed via [`MagrGC::make_weak`](super::heap::MagrGC::make_weak);
/// upgraded via [`MagrGC::upgrade_weak`](super::heap::MagrGC::upgrade_weak).
///
/// Atomic value variants (`I64` / `F64` / `Bool` / `Char` / `Str` / `Null`)
/// cannot be weak-referenced — `make_weak` returns `None` for them.
#[derive(Debug, Clone)]
pub struct WeakRef {
    pub(crate) inner: WeakRefInner,
}

#[derive(Debug, Clone)]
pub(crate) enum WeakRefInner {
    Object(WeakGcRef<ScriptObject>),
    Array (WeakGcRef<Vec<Value>>),
}

// ── GC handle table ──────────────────────────────────────────────────────────

/// Discriminator on slots in [`MagrGC::handle_alloc`](super::heap::MagrGC::handle_alloc).
///
/// - **`Strong`**: slot stores a reference that anchors the target across GC
///   collection — equivalent to C# `GCHandleType.Normal`. In Phase 1 RC mode
///   this is a `Rc::clone` of the wrapped value (atomic values are stored by
///   value clone). The slot keeps the target alive until the slot is freed.
/// - **`Weak`**: slot stores a non-anchoring reference; if all strong refs to
///   the target drop, [`MagrGC::handle_target`](super::heap::MagrGC::handle_target)
///   on the slot returns `None`. Equivalent to C# `GCHandleType.Weak`.
///
/// Phase 3 tracing GC will add `Pinned` (anchor + forbid relocation) and
/// `WeakTrackResurrection` (weak + finalizer-aware) variants.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcHandleKind {
    Weak,
    Strong,
}

// ── Stats ────────────────────────────────────────────────────────────────────

/// Heap-wide statistics snapshot returned by
/// [`MagrGC::stats`](super::heap::MagrGC::stats).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct HeapStats {
    /// Total allocations since heap creation.
    pub allocations:        u64,
    /// Number of `collect_cycles` / `force_collect` invocations.
    pub gc_cycles:          u64,
    /// Approximate live bytes. **RC mode caveat**: monotonically increases
    /// (`Rc<T>` drop is not observable); Phase 3 tracing GC will be precise.
    pub used_bytes:         u64,
    /// Heap upper bound (`None` = unlimited).
    pub max_bytes:          Option<u64>,
    /// Currently pinned roots (host-side `pin_root` count, includes frame pins).
    pub roots_pinned:       u64,
    /// Pending finalizer registrations (Phase 1: never invoked).
    pub finalizers_pending: u64,
    /// Active observers.
    pub observers:          u64,
}

// ── Heap snapshot ────────────────────────────────────────────────────────────

/// Coverage discriminator on [`HeapSnapshot`].
///
/// Phase 1 RC mode cannot enumerate all live objects (no global Rc registry);
/// the snapshot covers only objects reachable from currently-pinned roots.
/// Phase 3 tracing GC will produce `Full` snapshots.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SnapshotCoverage {
    Full,
    ReachableFromPinnedRoots,
}

impl Default for SnapshotCoverage {
    fn default() -> Self { Self::ReachableFromPinnedRoots }
}

/// Per-type aggregate in a [`HeapSnapshot`].
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ObjectStats {
    pub count: u64,
    pub bytes: u64,
}

/// Heap composition snapshot.
#[derive(Debug, Clone, Default)]
pub struct HeapSnapshot {
    /// Per type-name aggregate.
    pub objects_by_type: HashMap<String, ObjectStats>,
    pub total_objects:   u64,
    pub total_bytes:     u64,
    /// Microseconds since `RcMagrGC::EPOCH`.
    pub timestamp_us:    u64,
    pub coverage:        SnapshotCoverage,
}
