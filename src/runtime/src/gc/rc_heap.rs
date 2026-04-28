//! `RcMagrGC` —— Phase 1 默认 GC 后端，基于 `Rc<RefCell<...>>` 引用计数。
//!
//! 行为等价迁移前的直接构造（`Rc::new(RefCell::new(...))`），保留所有原有语义：
//! - `Rc::ptr_eq` 引用相等
//! - `Rc::as_ptr` 身份哈希
//! - `RefCell` 运行时借用检查
//!
//! **已知限制**：不解决环引用泄漏。Phase 2 由 `CycleCollectingHeap` 替代时修复。

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use crate::metadata::{NativeData, ScriptObject, TypeDesc, Value};

use super::heap::{HeapStats, MagrGC};

#[derive(Debug, Default)]
pub struct RcMagrGC {
    stats: RefCell<HeapStats>,
}

impl RcMagrGC {
    pub fn new() -> Self { Self::default() }

    fn bump_alloc(&self) {
        self.stats.borrow_mut().allocations += 1;
    }
}

impl MagrGC for RcMagrGC {
    fn alloc_object(
        &self,
        type_desc: Arc<TypeDesc>,
        slots: Vec<Value>,
        native: NativeData,
    ) -> Value {
        self.bump_alloc();
        Value::Object(Rc::new(RefCell::new(ScriptObject {
            type_desc, slots, native,
        })))
    }

    fn alloc_array(&self, elems: Vec<Value>) -> Value {
        self.bump_alloc();
        Value::Array(Rc::new(RefCell::new(elems)))
    }

    fn alloc_map(&self) -> Value {
        self.bump_alloc();
        Value::Map(Rc::new(RefCell::new(HashMap::new())))
    }

    fn collect_cycles(&self) {
        // Phase 1: 不做工作，仅递增 counter 让调用方可观察接口被触发。
        self.stats.borrow_mut().gc_cycles += 1;
    }

    fn stats(&self) -> HeapStats { *self.stats.borrow() }
}

#[cfg(test)]
#[path = "rc_heap_tests.rs"]
mod rc_heap_tests;
