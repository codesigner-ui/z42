//! `GcRef<T>` —— GC-managed heap reference (opaque handle).
//!
//! # 设计意图
//!
//! `GcRef<T>` 是 `Value::Object` / `Value::Array` 等堆引用类型的**不透明句柄**，
//! 隐藏内部 backing 实现。Phase 3 mark-sweep 替换 backing 时（`Rc<...>` →
//! 自定义堆 handle），所有 callsite 走 `GcRef::*` API 不需任何修改。
//!
//! # Phase 路线
//!
//! | Phase | backing |
//! |-------|---------|
//! | 3a | `Rc<RefCell<T>>` —— Rust std 引用计数 + 内部可变性（无 finalizer 钩子）|
//! | **3e（当前）** | `Rc<GcAllocation<T>>` —— GcAllocation 含 inner RefCell + finalizer Cell + 自定义 Drop。**Drop 时自动触发已注册的 finalizer**（one-shot）|
//! | 3+e（计划，可选）| 自定义堆 handle（index / pointer + region allocator + mark bits） |
//! | 4+ | 分代 / 并发 / MMTk 集成 |
//!
//! # API 契约
//!
//! - `clone()` 是 cheap operation（增加内部 refcount，类似 `Rc::clone`）
//! - `borrow()` / `borrow_mut()` 受 `RefCell` 借用规则约束（运行时 panic）
//! - `ptr_eq(a, b)` 比较是否指向同一堆分配
//! - `as_ptr(this)` 返回 GcAllocation 内 inner RefCell 的稳定地址（用作身份哈希）
//! - `downgrade(this)` 创建弱引用
//! - **Phase 3e**: 当最后一个 GcRef 被 Drop 时，若 finalizer 已注册自动触发

use std::cell::{Ref, RefCell, RefMut};
use std::rc::{Rc, Weak};

use super::types::FinalizerFn;

/// GC heap allocation wrapper. Phase 3e: holds the actual data plus a
/// per-object finalizer slot. `Drop` impl on this struct fires the finalizer
/// when the last `Rc<GcAllocation<T>>` reference goes away.
pub struct GcAllocation<T> {
    pub(crate) inner: RefCell<T>,
    /// Finalizer registered via `RcMagrGC::register_finalizer`. One-shot:
    /// `Drop` takes it from the cell so re-collection / re-drop never re-fires.
    pub(crate) finalizer: RefCell<Option<FinalizerFn>>,
}

impl<T> Drop for GcAllocation<T> {
    fn drop(&mut self) {
        // Take (not clone) → one-shot semantics
        if let Some(fin) = self.finalizer.borrow_mut().take() {
            fin();
        }
    }
}

/// GC-managed heap reference. **Phase 3e backing**: `Rc<GcAllocation<T>>`.
pub struct GcRef<T> {
    inner: Rc<GcAllocation<T>>,
}

impl<T> GcRef<T> {
    /// Allocate a new heap object holding `value`. Returns a strong reference.
    /// **Phase 3e**: 包一层 `GcAllocation` wrapper 提供 Drop-time finalizer 钩子。
    pub fn new(value: T) -> Self {
        Self {
            inner: Rc::new(GcAllocation {
                inner:     RefCell::new(value),
                finalizer: RefCell::new(None),
            }),
        }
    }

    /// Immutably borrow the inner value. Panics if a mutable borrow is active.
    pub fn borrow(&self) -> Ref<'_, T> { self.inner.inner.borrow() }

    /// Mutably borrow the inner value. Panics if any borrow is active.
    pub fn borrow_mut(&self) -> RefMut<'_, T> { self.inner.inner.borrow_mut() }

    /// True iff `a` and `b` point to the same heap allocation (reference equality).
    pub fn ptr_eq(a: &Self, b: &Self) -> bool {
        Rc::ptr_eq(&a.inner, &b.inner)
    }

    /// Stable pointer to the inner cell — used for identity hashing /
    /// finalizer / dedup keying. **Phase 3e**: 返回 GcAllocation 内的 inner
    /// 字段地址（稳定，分配后不变；不同分配地址不同）。
    pub fn as_ptr(this: &Self) -> *const RefCell<T> {
        let alloc_ptr: *const GcAllocation<T> = Rc::as_ptr(&this.inner);
        // SAFETY: alloc_ptr is from Rc::as_ptr, valid as long as `this` lives.
        // GcAllocation field offsets are stable (Rust default repr).
        unsafe { &(*alloc_ptr).inner as *const RefCell<T> }
    }

    /// Create a weak reference (does not prevent collection).
    pub fn downgrade(this: &Self) -> WeakGcRef<T> {
        WeakGcRef { inner: Rc::downgrade(&this.inner) }
    }

    /// Strong reference count of the underlying allocation.
    ///
    /// Used internally by the cycle collector (Phase 3c) to compute external
    /// reference counts. Pub(crate) since it leaks an implementation detail
    /// of the Phase 3e Rc<GcAllocation> backing.
    pub(crate) fn strong_count(this: &Self) -> usize {
        Rc::strong_count(&this.inner)
    }

    /// **Phase 3e**: 注册 finalizer。最后一个 GcRef Drop 时（含 cycle 断环
    /// 后 alive_vec drop）自动调用，并 take 出来（one-shot）。
    pub(crate) fn set_finalizer(this: &Self, fin: FinalizerFn) {
        *this.inner.finalizer.borrow_mut() = Some(fin);
    }

    /// **Phase 3e**: 取消已注册的 finalizer，返回 true 表示之前已注册。
    pub(crate) fn cancel_finalizer(this: &Self) -> bool {
        this.inner.finalizer.borrow_mut().take().is_some()
    }

    /// **Phase 3e**: 查询是否已注册 finalizer。
    pub(crate) fn has_finalizer(this: &Self) -> bool {
        this.inner.finalizer.borrow().is_some()
    }
}

impl<T> Clone for GcRef<T> {
    fn clone(&self) -> Self {
        Self { inner: Rc::clone(&self.inner) }
    }
}

impl<T: std::fmt::Debug> std::fmt::Debug for GcRef<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.inner.inner.try_borrow() {
            Ok(b)  => f.debug_tuple("GcRef").field(&*b).finish(),
            Err(_) => f.debug_tuple("GcRef").field(&"<borrowed>").finish(),
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
        self.inner.upgrade().map(|rc| GcRef { inner: rc })
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
