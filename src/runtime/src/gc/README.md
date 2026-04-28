# gc/

## 职责

z42 VM 的 GC 子系统：堆对象（`ScriptObject` / `Array` / `Map`）的分配与回收抽象。

不包含：根扫描、栈映射、写屏障真实实现 —— 这些留给 Phase 2/3。

## 核心文件

| 文件 | 职责 |
|------|------|
| `heap.rs` | `trait MagrGC` —— GC 抽象接口（参考 MMTk porting contract）；`HeapStats` —— 统计信息 |
| `rc_heap.rs` | `RcMagrGC` —— Phase 1 默认后端，`Rc<RefCell<...>>` 引用计数 |
| `heap_tests.rs` | trait 默认方法契约测试 |
| `rc_heap_tests.rs` | RcMagrGC 行为单元测试 |

## 入口点

- `crate::gc::MagrGC` —— GC 接口 trait
- `crate::gc::RcMagrGC` —— Phase 1 默认实现
- `crate::gc::HeapStats` —— 堆统计

实际使用通过 `VmContext::heap()`：

```rust
let v = ctx.heap().alloc_array(vec![Value::Null; n]);
let s = ctx.heap().stats();
```

## 依赖关系

- 上游：`metadata::{Value, ScriptObject, TypeDesc, NativeData}`
- 下游：`vm_context::VmContext` 持有 `Box<dyn MagrGC>`；`interp/exec_instr.rs` 与 `jit/helpers_object.rs` 通过 `ctx.heap()` 调用

## Phase 路线

详见 [`docs/design/vm-architecture.md`](../../../../docs/design/vm-architecture.md) "GC 子系统" 段。

**已知限制（Phase 1）**：

1. 环引用泄漏：`a.next = b; b.next = a` 仍泄漏 → Phase 2 修复
2. corelib 内 `Rc::new` 直构未迁移：`corelib/object.rs:34`、`corelib/fs.rs:51`、`corelib/tests.rs` 的 3 处 → Phase 1.5 配合 NativeFn 签名扩展一并处理
3. `alloc_map()` 接口已就位但暂无脚本驱动 callsite

## 命名

**MagrGC** 取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造定制行星的传奇世界。
