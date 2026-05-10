# Tasks: Introduce GcRef<T> Handle Abstraction (Phase 3a)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：refactor

## 变更说明

把 `Value::Object` / `Value::Array` 内部的 `Rc<RefCell<T>>` 隐藏到不透明句柄
`GcRef<T>`，弱引用同步抽象为 `WeakGcRef<T>`。**Phase 3a backing 仍是
`Rc<RefCell<T>>`，行为零变化**；这是 Phase 3b/c（自定义堆 + mark-sweep）
的物理前置 —— 让后续算法替换不再触动 callsite。

## 完成清单

### 阶段 1: gc/refs.rs（NEW）✅
- [x] `GcRef<T>` struct + new / borrow / borrow_mut / ptr_eq / as_ptr / downgrade / Clone / Debug
- [x] `WeakGcRef<T>` struct + upgrade / Clone / Debug
- [x] 模块 doc comment：Phase 3a 角色 + 当前 Rc<RefCell<T>> backing + Phase 3b/c/d/e 演进

### 阶段 2: gc/types.rs ✅
- [x] `WeakRefInner` 内部 `std::rc::Weak<RefCell<...>>` → `WeakGcRef<...>`

### 阶段 3: metadata/types.rs ✅
- [x] `use crate::gc::GcRef`
- [x] `Value::Object(Rc<RefCell<ScriptObject>>)` → `Value::Object(GcRef<ScriptObject>)`
- [x] `Value::Array(Rc<RefCell<Vec<Value>>>)` → `Value::Array(GcRef<Vec<Value>>)`
- [x] PartialEq Rc::ptr_eq → GcRef::ptr_eq
- [x] 注释行更新（Phase 3a backing 描述）

### 阶段 4: gc/rc_heap.rs / rc_heap_tests.rs ✅
- [x] `Value::Object(Rc::new(RefCell::new(...)))` → `Value::Object(GcRef::new(...))`
- [x] `Value::Array(Rc::new(RefCell::new(...)))` → `Value::Array(GcRef::new(...))`
- [x] `Rc::as_ptr(rc) as *const _ as usize` → `GcRef::as_ptr(gc) as *const _ as usize`
- [x] `Rc::downgrade(rc)` → `GcRef::downgrade(gc)`
- [x] 单元测试 `std::rc::Rc::ptr_eq` → `crate::gc::GcRef::ptr_eq`
- [x] 模块 doc comment 更新（提到 GcRef 句柄抽象）

### 阶段 5: callsite 模式替换 ✅
- [x] `corelib/object.rs:46` —— `Rc::ptr_eq` → `GcRef::ptr_eq`
- [x] `corelib/object.rs:58` —— `Rc::as_ptr` → `GcRef::as_ptr`
- [x] `corelib/object.rs:70` —— `Rc::ptr_eq` → `GcRef::ptr_eq`
- [x] interp / jit 中 `rc.clone()` / `rc.borrow()` / `rc.borrow_mut()` —— GcRef 提供同 API，无需修改

### 阶段 6: 模块导出 + 文档 ✅
- [x] `gc/mod.rs` re-export `GcRef` / `WeakGcRef`
- [x] `gc/mod.rs` 模块文档更新（提到 GcRef + Phase 3a/b/c/d/e 拆分路线）
- [x] `gc/README.md` 入口点段加 `GcRef<T>` / `WeakGcRef<T>`
- [x] `docs/design/vm-architecture.md` Phase 路线表加 Phase 2 跳过说明 + Phase 3a 已落地 + Phase 3b/c/d/e 拆分

### 阶段 7: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **120/120 通过**
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **740/740**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

**`Rc::*` 实际调用 grep 验证**：
全代码库唯一 `Rc::ptr_eq` / `Rc::as_ptr` / `Rc::downgrade` / `Rc<RefCell` 实际调用
仅在 `gc/refs.rs:35,55,61,66`（GcRef 内部权威实现）。其它命中均为文档注释引用历史。

### 结论：✅ 全绿，可以归档
