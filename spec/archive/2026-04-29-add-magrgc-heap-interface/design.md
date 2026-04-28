# Design: MagrGC Heap Interface

## Architecture

```
┌──────────────────────────────────────────────────────┐
│  interp/exec_instr.rs    jit/helpers_object.rs       │
│  (ArrayNew / ArrayNewLit / ObjNew callsites)         │
└────────────────┬─────────────────────────────────────┘
                 │ ctx.heap().alloc_*(...)
                 │ vm_ctx_ref(ctx).heap().alloc_*(...)
                 ▼
┌──────────────────────────────────────────────────────┐
│ VmContext.heap: Box<dyn MagrGC>  (gc/heap.rs)        │
│   alloc_object / alloc_array / alloc_map             │
│   write_barrier (no-op default)                      │
│   collect / collect_cycles (no-op default)           │
│   stats                                              │
└────────────────┬─────────────────────────────────────┘
                 │
                 ▼
┌──────────────────────────────────────────────────────┐
│ RcMagrGC (gc/rc_heap.rs)                             │
│   stats: RefCell<HeapStats>                          │
│   alloc_* → Rc::new(RefCell::new(...))               │
└──────────────────────────────────────────────────────┘
```

`VmContext` 持有 `Box<dyn MagrGC>`（与 `static_fields` / `lazy_loader` 同 ownership 模型）。
trait 方法签名 `&self`，内部可变性由实现自行用 `RefCell` 封装（Phase 1 仅 stats 计数器需要可变）。

## Decisions

### Decision 1: trait 形状参考 MMTk porting contract

**问题**：接口需要稳定到能换不同后端而不重写 callsite。

**选项**：
- A 极简（仅 alloc）—— 太薄，Phase 2/3 仍要扩
- B MMTk 风格（alloc + write_barrier + collect + stats）—— 工业标准
- C 自研 API —— 无先例

**决定**：B。MMTk `VMBinding` 是 OpenJDK / V8 / Julia / Ruby / RustPython 的事实标准；shape 兼容也为未来潜在的 MMTk 集成（Phase 4+）留下港接路径。Phase 1 把 `write_barrier` / `collect` / `collect_cycles` 全部提供 no-op 默认实现，让 RcMagrGC 仅需实现 `alloc_*` + `stats` 两组方法。

### Decision 2: Phase 1 后端选 RcMagrGC，不引入 dumpster crate

**问题**：是否本次就修复环泄漏（引入 dumpster 2.0 或自研 Bacon-Rajan）？

**选项**：
- A `RcMagrGC`（行为等价现状，环仍泄漏）
- B `DumpsterMagrGC`（RC + 并发环检测，依赖外部 crate）
- C 自研 Bacon-Rajan

**决定**：A。Phase 1 范围严格限定为"接口收口、行为零变化"；环检测算法选型 + 新 crate 依赖应在 Phase 2 单独 spec 中讨论。环泄漏作为已知限制写入 README + vm-architecture.md。这样 Phase 1 commit 范围干净（纯重构），失败回滚成本低。

### Decision 3: Value enum 形状不变

**问题**：是否本次把 `Value::Array(Rc<RefCell<...>>)` 改成 `Value::Array(GcRef<T>)`？

**决定**：不改。Phase 1 零 Value 侵入；Phase 3 mark-sweep 实施时才需要句柄抽象（届时 PartialEq / JIT helper / 测试构造都需要适配）。Phase 1 trait 方法返回 `Value`，把"包装成什么"完全交给实现 —— 未来 Phase 3 的实现可改为返回 `Value::Array(GcRef<...>)`。

### Decision 4: alloc_map 接口先占位但本次不强制迁移

**问题**：grep 未发现脚本驱动的 `Rc::new(RefCell::new(HashMap...))` 分配点。

**决定**：trait 上加 `alloc_map() -> Value` 占位（避免后续接口扩展打破 ABI）；本次不强制迁移因为没有 callsite。未来某 IR 指令真正分配 Map（如 `MapNew`）时直接走接口。

### Decision 5: heap 字段不用 RefCell

**问题**：是否参考 `lazy_loader: RefCell<Option<LazyLoader>>` 模式？

**决定**：不用。`lazy_loader` 是 lazy install（创建 ctx 时 None，后续 install），所以需要 `RefCell<Option<...>>`；`heap` 在 `VmContext::new()` 立即构造且生命周期内不替换。所有 trait 方法走 `&self`，内部可变性由 `RcMagrGC.stats: RefCell<HeapStats>` 承担。如果未来需要"运行时替换 heap 实现"，再加 RefCell 包装。

### Decision 6: corelib 内的 Rc::new 暂不迁移

**问题**：`corelib/object.rs:34`（`__obj_get_type`）和 `corelib/fs.rs:51`（`__env_args`）也使用 `Rc::new(RefCell::new(...))`。

**决定**：本次 Phase 1 **不迁移**。原因：corelib `NativeFn = fn(&[Value]) -> Result<Value>` 签名不携带 `VmContext` 引用，要让它访问 heap 需要扩展 NativeFn 签名 + 修改全部 ~30 个 builtin —— 这是独立的"NativeFn ABI 扩展"问题，不属于 GC 接口收口。Phase 1.5（独立 spec）专门处理 corelib + tests 的迁移。

文档明确：Phase 1 完成后剩余 ~3 处 `Rc::new(RefCell::new(...))` 直构（corelib/object.rs / corelib/fs.rs / corelib/tests.rs），属于已知技术债。

## Roadmap & Future Work

写入 `docs/design/vm-architecture.md` 新增 "GC 子系统" 段：

| Phase | 内容 | 状态 |
|-------|------|------|
| **Phase 1** | `trait MagrGC` 接口 + `RcMagrGC` 实现 + 6 个脚本驱动 callsite 收口 | 本次 spec |
| **Phase 1.5** | corelib NativeFn 签名扩展带 `&VmContext` + corelib 内剩余 Rc::new 迁移 | 待立项 |
| **Phase 2** | 环检测真实实现（dumpster 2.0 集成 / 自研 Bacon-Rajan 二选一） | 待立项 |
| **Phase 3** | Mark-Sweep + RootScope + 真实 write_barrier + Cranelift stack maps；`Value::Array/Map/Object` 改用 `GcRef<T>` | 待立项 |
| **Phase 4+** | 分代 / 并发 / MMTk 集成 | 长期 |

**已知技术债（Phase 1 完成后留存）**：

1. **环引用泄漏**：`a.next = b; b.next = a` 仍泄漏 → Phase 2 修复
2. **alloc_map 暂无 callsite**：接口占位但无迁移 → 实际分配点出现时迁移
3. **corelib 直构**：`corelib/object.rs:34`、`corelib/fs.rs:51`、`corelib/tests.rs:19/68/77` 仍直接 `Rc::new(RefCell::new(...))` → Phase 1.5 收口
4. **测试 bypass**：`corelib/tests.rs` 测试直接构造 Value 旁路接口 → 与 corelib 一并 Phase 1.5 处理

**未来动机记录**（写入 vm-architecture.md GC 子系统段）：

> 当 Phase 2/3 GC 成熟（环检测 + 追踪），字符串可以从 `Value::Str(String)` primitive
> 迁移成 `Value::Object(...)` 包装的脚本类（z42 源码实现 BCL `String`），届时 z42
> 源码可承担更多 string 方法实现，进一步减少 Rust 端硬编码 builtin。这与
> 2026-04-24 起的 simplify-string-stdlib / wave1-string-script 系列重构方向一致。

## Implementation Notes

### gc/heap.rs

```rust
use std::sync::Arc;
use crate::metadata::{NativeData, TypeDesc, Value};

/// Heap statistics — Phase 1 暴露基础计数；Phase 2+ 扩展存活对象数等。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct HeapStats {
    /// Total allocation count since heap creation.
    pub allocations: u64,
    /// Number of `collect_cycles()` invocations.
    pub gc_cycles: u64,
}

/// MagrGC — z42 VM 的 GC 抽象接口。
///
/// 命名取自《银河系漫游指南》中的 Magrathea —— 那颗专门建造定制行星的传奇世界。
/// trait 形状参考 MMTk `VMBinding`，便于未来切换实现而无需改 callsite。
pub trait MagrGC: std::fmt::Debug {
    fn alloc_object(
        &self,
        type_desc: Arc<TypeDesc>,
        slots: Vec<Value>,
        native: NativeData,
    ) -> Value;

    fn alloc_array(&self, elems: Vec<Value>) -> Value;
    fn alloc_map(&self) -> Value;

    /// Phase 2+ 用：写屏障。Phase 1 默认 no-op。
    fn write_barrier(&self, _owner: &Value, _slot: usize, _new: &Value) {}

    /// Phase 3+ 用：触发完整 GC。Phase 1 默认 no-op。
    fn collect(&self) {}

    /// Phase 2+ 用：触发环检测。Phase 1 默认 no-op（仅递增 stats counter）。
    fn collect_cycles(&self) {}

    fn stats(&self) -> HeapStats;
}
```

### gc/rc_heap.rs

```rust
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use crate::metadata::{NativeData, ScriptObject, TypeDesc, Value};
use super::heap::{HeapStats, MagrGC};

/// Phase 1 默认后端：Rc<RefCell<...>> 引用计数堆。
///
/// **限制**：不解决环引用泄漏；Phase 2 由 CycleCollectingHeap 替代。
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
        // Phase 1: no-op；仍递增 stats.gc_cycles 以便观察接口被调用。
        self.stats.borrow_mut().gc_cycles += 1;
    }

    fn stats(&self) -> HeapStats { *self.stats.borrow() }
}
```

### VmContext 集成

```rust
pub struct VmContext {
    pub(crate) static_fields:     RefCell<HashMap<String, Value>>,
    pub(crate) pending_exception: RefCell<Option<Value>>,
    pub(crate) lazy_loader:       RefCell<Option<LazyLoader>>,
    pub(crate) heap:              Box<dyn MagrGC>,
}

impl VmContext {
    pub fn new() -> Self {
        Self {
            static_fields:     RefCell::new(HashMap::new()),
            pending_exception: RefCell::new(None),
            lazy_loader:       RefCell::new(None),
            heap:              Box::new(RcMagrGC::new()),
        }
    }

    pub fn heap(&self) -> &dyn MagrGC {
        self.heap.as_ref()
    }
}
```

### 迁移示例

**interp/exec_instr.rs**：

```rust
// before
frame.set(*dst, Value::Array(Rc::new(RefCell::new(vec![Value::Null; n]))));

// after
frame.set(*dst, ctx.heap().alloc_array(vec![Value::Null; n]));
```

**jit/helpers_object.rs**：

```rust
// before
(*frame).regs[dst as usize] = Value::Array(Rc::new(RefCell::new(vec![Value::Null; n])));

// after
let vm_ctx = vm_ctx_ref(ctx);
(*frame).regs[dst as usize] = vm_ctx.heap().alloc_array(vec![Value::Null; n]);
```

## Testing Strategy

- **单元测试** (`gc/heap_tests.rs`)：trait 默认方法行为 —— `write_barrier` / `collect` / `collect_cycles` 默认实现 no-op、不 panic
- **单元测试** (`gc/rc_heap_tests.rs`)：RcMagrGC 行为 —— 多次 alloc 返回独立 Rc、stats 计数正确、Object/Array/Map 形状对、`collect_cycles` 仅递增 counter
- **集成测试** (`vm_context_tests.rs`)：`heap()` accessor、两个 ctx 实例的 heap 隔离
- **回归验证**：`dotnet test` + `./scripts/test-vm.sh` 必须 100% 通过 → 证明 6 个 callsite 迁移行为等价（如有任何 golden test 失败，说明迁移引入了非等价行为）
