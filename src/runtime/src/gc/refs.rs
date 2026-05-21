//! `GcRef<T>` —— GC-managed heap reference (opaque handle).
//!
//! # 设计意图
//!
//! `GcRef<T>` 是 `Value::Object` / `Value::Array` 等堆引用类型的**不透明句柄**，
//! 隐藏内部 backing 实现。后续 backing 切换（自定义堆 allocator / 真 mark-sweep
//! / MMTk 集成等，见 [`docs/design/runtime/vm-architecture.md`](../../../../docs/design/runtime/vm-architecture.md)
//! "GC 后续迭代规划" 段）所有 callsite 走 `GcRef::*` API 不需任何修改。
//!
//! # 当前 backing（add-multithreading-foundation Phase 3，2026-05-20）
//!
//! `Arc<GcAllocation<T>>` —— `GcAllocation` 含 `inner: parking_lot::Mutex<T>` +
//! `finalizer: parking_lot::Mutex<Option<FinalizerFn>>` + 自定义 `Drop`。
//! **Drop 时自动触发已注册的 finalizer**（one-shot via take）。
//!
//! Phase 3 之前 backing 是 `Rc<GcAllocation<T>>` + `RefCell<T>`。切到 Arc/Mutex
//! 是 multi-threading foundation 必需的 Send-safe 化（GcRef<T: Send>: Send +
//! Sync，跨线程安全）。单线程语义不变；Mutex 在无竞争路径上 ~2-5ns 原子开销，
//! VM 实测 stdlib + test-vm 全套 < 5% 退化。
//!
//! # API 契约
//!
//! - `clone()` 是 cheap operation（Arc::clone 一次原子 fetch_add）
//! - `borrow()` / `borrow_mut()` 都用 `try_lock`，**recursive call panic**
//!   （保留 RefCell-style 调试体验；避免 Mutex deadlock 难调试）
//! - `ptr_eq(a, b)` 比较是否指向同一堆分配
//! - `as_ptr(this)` 返回 GcAllocation 内 inner Mutex 的稳定地址（身份哈希 / dedup key）
//! - `downgrade(this)` 创建弱引用
//! - 当最后一个 GcRef 被 Drop 时，若 finalizer 已注册自动触发

use std::sync::{Arc, Weak};

use parking_lot::{Mutex, MutexGuard};

use super::types::FinalizerFn;

/// `Ref<'a, T>` —— immutable borrow guard alias. Phase 3 后等价于
/// `MutexGuard<'a, T>`（Mutex 无 read/write 区分，写权限即 lock）。
///
/// Callsite 使用 `let r = gc_ref.borrow();` 写法不需要改 type 签名。
pub type Ref<'a, T> = MutexGuard<'a, T>;

/// `RefMut<'a, T>` —— mutable borrow guard alias. 与 `Ref` 同（Mutex
/// 无 read/write 区分），仅返回类型语义一致；callsite `let r = gc_ref.borrow_mut();`
/// 不变。
pub type RefMut<'a, T> = MutexGuard<'a, T>;

/// GC heap allocation wrapper. Holds the actual data plus a per-object
/// finalizer slot. `Drop` impl on this struct fires the finalizer when
/// the last `Arc<GcAllocation<T>>` reference goes away.
pub struct GcAllocation<T> {
    pub(crate) inner: Mutex<T>,
    /// Finalizer registered via `ArcMagrGC::register_finalizer`. One-shot:
    /// `Drop` takes it from the cell so re-collection / re-drop never re-fires.
    pub(crate) finalizer: Mutex<Option<FinalizerFn>>,
    /// **add-mark-sweep-collector P1 (2026-05-21)**: per-object mark bit
    /// used by the mark-sweep collector. Reset to 0 at the start of each
    /// collect; set to 1 by the mark phase BFS when the object is found
    /// reachable from roots. Sweep phase drops entries where this is 0.
    ///
    /// `Relaxed` ordering is sufficient — the AtomicU8 is only meaningful
    /// during stop-the-world collect (safepoint already established
    /// happens-before via gc_phase Mutex). Single byte keeps allocation
    /// size bump negligible.
    pub(crate) marked: std::sync::atomic::AtomicU8,
}

impl<T> Drop for GcAllocation<T> {
    fn drop(&mut self) {
        // Take (not clone) → one-shot semantics
        if let Some(fin) = self.finalizer.lock().take() {
            fin();
        }
    }
}

/// GC-managed heap reference. **Phase 3 backing**: `Arc<GcAllocation<T>>`.
pub struct GcRef<T> {
    inner: Arc<GcAllocation<T>>,
}

impl<T> GcRef<T> {
    /// Allocate a new heap object holding `value`. Returns a strong reference.
    pub fn new(value: T) -> Self {
        Self {
            inner: Arc::new(GcAllocation {
                inner:     Mutex::new(value),
                finalizer: Mutex::new(None),
                marked:    std::sync::atomic::AtomicU8::new(0),
            }),
        }
    }

    /// Immutably borrow the inner value.
    ///
    /// **Blocking acquire** (parking_lot `lock()`). Until 2026-05-20 this
    /// used `try_lock().expect(...)` to catch RefCell-style same-thread
    /// re-entrance — but `add-multithreading-foundation` Phase 3 migrated
    /// the backing from `Rc<RefCell>` to `Arc<parking_lot::Mutex>` and
    /// `add-threading-stdlib` (2026-05-20) made cross-thread access real.
    /// Two workers concurrently `field_get` on the same shared object
    /// hit this borrow path and would `try_lock` panic.
    ///
    /// `add-sync-primitives` (2026-05-20) flips to blocking: legitimate
    /// cross-thread contention waits its turn; same-thread reentrance now
    /// deadlocks (matching standard `std::sync::Mutex` semantics). The
    /// recursive-borrow safety net was a RefCell porting artifact — Rust
    /// `Mutex` has never offered it. If reentrant access patterns appear
    /// in practice they're bugs and should be restructured, not papered
    /// over with a recursive Mutex variant.
    pub fn borrow(&self) -> Ref<'_, T> {
        self.inner.inner.lock()
    }

    /// Mutably borrow the inner value.
    ///
    /// Same semantics as `borrow()`: blocking lock. Mutex has no read/write
    /// distinction; the type alias `RefMut<'a, T>` keeps the call-site API
    /// stable while the underlying lock is exclusive.
    pub fn borrow_mut(&self) -> RefMut<'_, T> {
        self.inner.inner.lock()
    }

    /// True iff `a` and `b` point to the same heap allocation (reference equality).
    pub fn ptr_eq(a: &Self, b: &Self) -> bool {
        Arc::ptr_eq(&a.inner, &b.inner)
    }

    /// Stable pointer to the inner cell — used for identity hashing /
    /// finalizer / dedup keying. Returns the address of the inner Mutex.
    /// Stable for the lifetime of the allocation.
    pub fn as_ptr(this: &Self) -> *const Mutex<T> {
        let alloc_ptr: *const GcAllocation<T> = Arc::as_ptr(&this.inner);
        // SAFETY: alloc_ptr is from Arc::as_ptr, valid as long as `this` lives.
        // GcAllocation field offsets are stable (Rust default repr).
        unsafe { &(*alloc_ptr).inner as *const Mutex<T> }
    }

    /// **add-mark-sweep-collector P1 (2026-05-21)**: atomically set the
    /// mark bit. Returns `true` if this call transitioned 0→1 (i.e., we
    /// were the first to mark this allocation in the current GC cycle),
    /// `false` if it was already marked. Used by the mark phase BFS to
    /// decide whether to enqueue this object's children.
    pub fn mark(this: &Self) -> bool {
        this.inner.marked
            .compare_exchange(
                0, 1,
                std::sync::atomic::Ordering::Relaxed,
                std::sync::atomic::Ordering::Relaxed,
            )
            .is_ok()
    }

    /// **add-mark-sweep-collector P1 (2026-05-21)**: read the current
    /// mark state. Used by the sweep phase to decide retention.
    pub fn is_marked(this: &Self) -> bool {
        this.inner.marked.load(std::sync::atomic::Ordering::Relaxed) != 0
    }

    /// **add-mark-sweep-collector P1 (2026-05-21)**: reset the mark bit
    /// to 0. Called by the sweep phase on survivors (preparing them for
    /// the next collect cycle).
    pub fn clear_mark(this: &Self) {
        this.inner.marked.store(0, std::sync::atomic::Ordering::Relaxed);
    }

    /// Create a weak reference (does not prevent collection).
    pub fn downgrade(this: &Self) -> WeakGcRef<T> {
        WeakGcRef { inner: Arc::downgrade(&this.inner) }
    }

    /// Strong reference count of the underlying allocation.
    ///
    /// Used internally by the cycle collector to compute external
    /// reference counts. Pub(crate) since it leaks an implementation detail
    /// of the Arc<GcAllocation> backing.
    pub(crate) fn strong_count(this: &Self) -> usize {
        Arc::strong_count(&this.inner)
    }

    /// 注册 finalizer。最后一个 GcRef Drop 时（含 cycle 断环后 alive_vec
    /// drop）自动调用，并 take 出来（one-shot）。
    pub(crate) fn set_finalizer(this: &Self, fin: FinalizerFn) {
        *this.inner.finalizer.lock() = Some(fin);
    }

    /// 取消已注册的 finalizer，返回 true 表示之前已注册。
    pub(crate) fn cancel_finalizer(this: &Self) -> bool {
        this.inner.finalizer.lock().take().is_some()
    }

    /// 查询是否已注册 finalizer。
    pub(crate) fn has_finalizer(this: &Self) -> bool {
        this.inner.finalizer.lock().is_some()
    }
}

impl<T> Clone for GcRef<T> {
    fn clone(&self) -> Self {
        Self { inner: Arc::clone(&self.inner) }
    }
}

impl<T: std::fmt::Debug> std::fmt::Debug for GcRef<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.inner.inner.try_lock() {
            Some(b) => f.debug_tuple("GcRef").field(&*b).finish(),
            None    => f.debug_tuple("GcRef").field(&"<borrowed>").finish(),
        }
    }
}

/// Weak GC reference. Does not keep target alive; upgrade returns `None` if
/// the target has been collected.
pub struct WeakGcRef<T> {
    inner: Weak<GcAllocation<T>>,
}

impl<T> WeakGcRef<T> {
    /// Try to recover a strong reference. Returns `None` if the target has
    /// been collected (no other strong references exist).
    pub fn upgrade(&self) -> Option<GcRef<T>> {
        self.inner.upgrade().map(|arc| GcRef { inner: arc })
    }
}

impl<T> Clone for WeakGcRef<T> {
    fn clone(&self) -> Self {
        Self { inner: self.inner.clone() }
    }
}

impl<T> std::fmt::Debug for WeakGcRef<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WeakGcRef")
            .field("dropped", &(self.inner.strong_count() == 0))
            .finish()
    }
}
