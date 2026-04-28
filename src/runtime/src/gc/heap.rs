//! `MagrGC` —— z42 VM 的 GC 抽象接口。
//!
//! 命名取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造定制行星的传奇
//! 世界，与"管理对象生命周期"主题契合。
//!
//! # 设计
//!
//! trait 形状参考 [MMTk](https://www.mmtk.io/) 的 `VMBinding` porting contract：
//! `alloc_*` / `write_barrier` / `collect` / `stats`。这是 OpenJDK / V8 / Julia /
//! Ruby / RustPython 的事实标准接口形状，便于未来切换实现而无需改动 callsite。
//!
//! # Phase 路线（详见 `docs/design/vm-architecture.md`）
//!
//! - **Phase 1（本文件 + `rc_heap.rs`）**：接口定义 + `RcMagrGC` 等价当前
//!   `Rc<RefCell<...>>` 行为；环引用仍泄漏（已知限制）。
//! - **Phase 2**：`collect_cycles()` 真实实现（Bacon-Rajan / dumpster 2.0）。
//! - **Phase 3**：Mark-Sweep + 真实 `write_barrier` + `Value::Array/Map/Object`
//!   改用 `GcRef<T>` + Cranelift stack maps。
//! - **Phase 4+**：分代 / 并发 / MMTk 集成。

use std::sync::Arc;

use crate::metadata::{NativeData, TypeDesc, Value};



/// 堆统计信息。Phase 1 暴露基础计数；Phase 2+ 扩展存活对象数 / 暂停时长等。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct HeapStats {
    /// Total allocation count since heap creation.
    pub allocations: u64,
    /// Number of `collect_cycles()` invocations.
    pub gc_cycles: u64,
}

/// MagrGC —— z42 VM 的 GC 抽象接口。
///
/// 所有实现必须保证：
/// - `alloc_*` 返回的 `Value` 与对应 variant 一致（Object / Array / Map）
/// - 多次 `alloc_*` 返回的堆对象互相独立（`Rc::ptr_eq` / `GcRef::ptr_eq` 为 false）
/// - 实现自己负责 `&self` 接口背后的内部可变性（如计数器需 `RefCell`）
pub trait MagrGC: std::fmt::Debug {
    /// 分配一个 `ScriptObject` 并以 `Value::Object` 返回。
    fn alloc_object(
        &self,
        type_desc: Arc<TypeDesc>,
        slots: Vec<Value>,
        native: NativeData,
    ) -> Value;

    /// 分配一个 `Vec<Value>` 数组并以 `Value::Array` 返回。
    fn alloc_array(&self, elems: Vec<Value>) -> Value;

    /// 写屏障：当 `owner` 对象的 `slot` 字段被赋为 `new` 时通知 GC。
    ///
    /// Phase 2+ 用于 generational / concurrent GC 的卡表 / SATB 维护；
    /// Phase 1 默认 no-op。
    fn write_barrier(&self, _owner: &Value, _slot: usize, _new: &Value) {}

    /// 触发完整 GC（stop-the-world tracing）。Phase 1 默认 no-op。
    fn collect(&self) {}

    /// 触发环引用检测与回收。Phase 1 默认 no-op（仅递增 stats counter
    /// 以便观察接口被调用）。
    fn collect_cycles(&self) {}

    /// 当前堆统计。
    fn stats(&self) -> HeapStats;
}

#[cfg(test)]
#[path = "heap_tests.rs"]
mod heap_tests;
