//! `GcRef<T>` —— GC-managed heap reference (opaque handle).
//!
//! # 设计意图
//!
//! `GcRef<T>` 是 `Value::Object` / `Value::Array` 等堆引用类型的**不透明句柄**，
//! 隐藏内部 backing 实现。Phase 3 mark-sweep 替换 backing 时（`Rc<RefCell<T>>`
//! → 自定义堆 handle），所有 callsite 走 `GcRef::*` API 不需任何修改。
//!
//! 这是 "abstract before mutate" 教科书式重构：先把可能被替换的实现细节
//! 包到稳定接口背后，再换里面的内容。
//!
//! # Phase 路线
//!
//! | Phase | backing |
//! |-------|---------|
//! | **3a（本文件当前）**| `Rc<RefCell<T>>` —— Rust std 引用计数 + 内部可变性 |
//! | 3b | 自定义堆 handle（index / pointer + region allocator）|
//! | 3c | + mark bit 元数据；环引用真实回收 |
//! | 3d | + finalizer 真实触发 + iterate_live_objects 全堆覆盖 |
//! | 3e | + Cranelift stack maps（JIT 路径下 GC 安全点）|
//!
//! # API 契约
//!
//! - `clone()` 是 cheap operation（增加内部 refcount，类似 `Rc::clone`）
//! - `borrow()` / `borrow_mut()` 受 `RefCell` 借用规则约束（运行时 panic）
//! - `ptr_eq(a, b)` 比较是否指向同一堆分配
//! - `as_ptr(this)` 返回内部指针，用作身份哈希 / finalizer key（值稳定，allocation 不移位）
//! - `downgrade(this)` 创建弱引用，避免人为环引用泄漏（Phase 3c 真实环检测前的手动 workaround）

use std::cell::{Ref, RefCell, RefMut};
use std::rc::{Rc, Weak};

/// GC-managed heap reference. **Phase 3a backing**: `Rc<RefCell<T>>`.
pub struct GcRef<T> {
    inner: Rc<RefCell<T>>,
}

impl<T> GcRef<T> {
    /// Allocate a new heap object holding `value`. Returns a strong reference.
    ///
    /// **Phase 3a**: Wraps `Rc::new(RefCell::new(value))`. Phase 3b will route
    /// through the GC heap allocator instead.
    pub fn new(value: T) -> Self {
        Self { inner: Rc::new(RefCell::new(value)) }
    }

    /// Immutably borrow the inner value. Panics if a mutable borrow is active.
    pub fn borrow(&self) -> Ref<'_, T> { self.inner.borrow() }

    /// Mutably borrow the inner value. Panics if any borrow is active.
    pub fn borrow_mut(&self) -> RefMut<'_, T> { self.inner.borrow_mut() }

    /// True iff `a` and `b` point to the same heap allocation (reference equality).
    pub fn ptr_eq(a: &Self, b: &Self) -> bool {
        Rc::ptr_eq(&a.inner, &b.inner)
    }

    /// Stable pointer to the inner cell. Used for identity hashing and
    /// finalizer keying (allocation does not move under RC backing).
    pub fn as_ptr(this: &Self) -> *const RefCell<T> {
        Rc::as_ptr(&this.inner)
    }

    /// Create a weak reference (does not prevent collection).
    pub fn downgrade(this: &Self) -> WeakGcRef<T> {
        WeakGcRef { inner: Rc::downgrade(&this.inner) }
    }

    /// Strong reference count of the underlying allocation.
    ///
    /// Used internally by the cycle collector (Phase 3c) to compute external
    /// reference counts. Pub(crate) since it leaks an implementation detail
    /// of the Phase 3a Rc backing — Phase 3e+ may replace this with a
    /// generation-counter scheme.
    pub(crate) fn strong_count(this: &Self) -> usize {
        Rc::strong_count(&this.inner)
    }
}

impl<T> Clone for GcRef<T> {
    fn clone(&self) -> Self {
        Self { inner: Rc::clone(&self.inner) }
    }
}

impl<T: std::fmt::Debug> std::fmt::Debug for GcRef<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.inner.try_borrow() {
            Ok(b)  => f.debug_tuple("GcRef").field(&*b).finish(),
            Err(_) => f.debug_tuple("GcRef").field(&"<borrowed>").finish(),
        }
    }
}

/// Weak GC reference. Does not keep target alive; upgrade returns `None` if
/// the target has been collected.
pub struct WeakGcRef<T> {
    inner: Weak<RefCell<T>>,
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
