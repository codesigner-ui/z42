// Garbage collector — memory management for heap-allocated z42 objects.
//
// Phase 1 uses `Rc<RefCell<T>>` reference counting in the interpreter;
// this module will provide a tracing GC for Phase 3+ when cycles matter.
