# Spec: GC Heap Interface (MagrGC)

## ADDED Requirements

### Requirement: trait MagrGC 定义堆分配/回收接口

#### Scenario: alloc_object 创建 ScriptObject 并包装为 Value::Object
- **WHEN** 调用方执行 `heap.alloc_object(type_desc, slots, native)`
- **THEN** 返回 `Value::Object(...)`，其中内部 `ScriptObject` 的 `type_desc` / `slots` / `native` 字段与入参完全一致

#### Scenario: alloc_array 创建带初始元素的 Array
- **WHEN** 调用方执行 `heap.alloc_array(vec![Value::I64(1), Value::I64(2)])`
- **THEN** 返回 `Value::Array(...)`，其中内部 `Vec<Value>` 与入参一致

#### Scenario: alloc_map 创建空 Map
- **WHEN** 调用方执行 `heap.alloc_map()`
- **THEN** 返回 `Value::Map(...)`，其中内部 `HashMap<String, Value>` 为空

#### Scenario: 默认方法 collect / collect_cycles / write_barrier 在 Phase 1 是 no-op
- **WHEN** 调用方执行 `heap.collect()` / `heap.collect_cycles()` / `heap.write_barrier(&owner, 0, &new)`
- **THEN** 不 panic、不改变可观察状态（Phase 1 限制，文档明确说明）

### Requirement: RcMagrGC 是 Phase 1 默认后端

#### Scenario: VmContext::new 默认安装 RcMagrGC
- **WHEN** `VmContext::new()` 被调用
- **THEN** `ctx.heap()` 返回 `&dyn MagrGC`，trait object 背后是 `RcMagrGC` 实例

#### Scenario: alloc_object 通过 RcMagrGC 走 Rc<RefCell<...>>
- **WHEN** `RcMagrGC` 处理 `alloc_object(td, slots, NativeData::None)`
- **THEN** 返回的 `Value::Object` 内部为 `Rc<RefCell<ScriptObject{td, slots, NativeData::None}>>`，与迁移前的直接构造行为等价（含引用相等语义 `Rc::ptr_eq`）

#### Scenario: 多次 alloc 返回独立的 Rc 实例
- **WHEN** 同一 `RcMagrGC` 实例上连续调用 `alloc_array(vec![])` 两次得到 `a` 和 `b`
- **THEN** 提取出的 Rc 不满足 `Rc::ptr_eq(&a, &b)`（指针相等性正确）

### Requirement: 所有脚本驱动分配点收口到 heap 接口

#### Scenario: interp ArrayNew 走接口
- **WHEN** interp 执行 `Instruction::ArrayNew { dst, size }` (size=3)
- **THEN** 分配走 `ctx.heap().alloc_array(vec![Value::Null; 3])`，源码不再出现直接 `Rc::new(RefCell::new(...))`

#### Scenario: interp ArrayNewLit 走接口
- **WHEN** interp 执行 `Instruction::ArrayNewLit { dst, elems }`
- **THEN** 分配走 `ctx.heap().alloc_array(vals)`

#### Scenario: interp ObjNew 走接口
- **WHEN** interp 执行 `Instruction::ObjNew`
- **THEN** ScriptObject 分配走 `ctx.heap().alloc_object(type_desc, slots, NativeData::None)`

#### Scenario: JIT helpers 走接口
- **WHEN** JIT 编译代码调用 `jit_array_new` / `jit_array_new_lit` / `jit_obj_new`
- **THEN** 分配通过 `vm_ctx_ref(ctx).heap().alloc_*(...)` 走 heap 接口

### Requirement: HeapStats 暴露基础计数

#### Scenario: stats.allocations 单调递增
- **WHEN** `RcMagrGC` 上连续 N 次 alloc
- **THEN** `heap.stats().allocations == N`（前置 stats=0）

#### Scenario: stats.gc_cycles 反映 collect_cycles 调用次数
- **WHEN** Phase 1 的 RcMagrGC 上调用 `collect_cycles()` 一次
- **THEN** `heap.stats().gc_cycles == 1`（counter 仍递增，即使实际未做工作）

### Requirement: VmContext 提供 heap 访问

#### Scenario: ctx.heap() 返回 trait object 引用
- **WHEN** 用户代码调用 `ctx.heap()`
- **THEN** 返回 `&dyn MagrGC`，可直接调用 `alloc_*` / `stats()` 等接口方法

#### Scenario: 两个 VmContext 实例的 heap 完全隔离
- **WHEN** `ctx1.heap().alloc_array(vec![])` 调用一次后查 `ctx2.heap().stats()`
- **THEN** `ctx2.stats().allocations == 0`（与 lazy_loader / static_fields 隔离一致）

## IR Mapping

不引入新 IR 指令；`ArrayNew` / `ArrayNewLit` / `ObjNew` 在 IR 层不变，仅 VM 后端的分配实现切换。

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] VM interp
- [x] VM JIT helpers
