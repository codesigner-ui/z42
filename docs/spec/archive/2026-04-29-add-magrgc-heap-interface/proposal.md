# Proposal: Add MagrGC Heap Interface

## Why

当前 VM 堆分配散落在 6 个 callsite 直接构造 `Rc::new(RefCell::new(...))`，没有抽象层。已知缺陷：

- 环引用泄漏（`a.next = b; b.next = a`）
- 无法替换分配器实现
- 未来 stdlib 演进（如更多字符串方法移到 z42 脚本实现）会进一步增加分配点

roadmap 固定决策"z42 始终带 GC"已就位，A6 backlog 原排在 L3 async；本次提前到 L2 M7，以最小代价（接口收口、行为零变化）开启 GC 子系统迭代轨道。

命名 **MagrGC** 借鉴《银河系漫游指南》中的 Magrathea —— 那颗专门建造定制行星的传奇世界，与"管理对象生命周期"主题契合，且 Google 搜索无同名实现冲突。

## What Changes

- 定义 `trait MagrGC`，接口形状借鉴 MMTk porting contract（OpenJDK / V8 / Julia / Ruby 事实标准）
- 实现 `RcMagrGC`：Phase 1 默认后端，行为等价当前 `Rc<RefCell<...>>` 模型
- 6 个**脚本驱动**分配 callsite 全部迁移走 `ctx.heap().alloc_*()`（interp 3 + jit 3）
- `VmContext` 持有 `Box<dyn MagrGC>`
- `vm-architecture.md` 新增 "GC 子系统" 段，记录 Phase 1–4 多阶段路线图
- 字符串脚本化作为 Phase 2/3 GC 成熟后的动机记录到文档

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/mod.rs` | MODIFY | 移除 stub，re-export trait 与实现 |
| `src/runtime/src/gc/heap.rs` | NEW | `trait MagrGC` + `HeapStats` |
| `src/runtime/src/gc/rc_heap.rs` | NEW | `RcMagrGC` 默认实现 |
| `src/runtime/src/gc/heap_tests.rs` | NEW | trait 默认方法契约测试 |
| `src/runtime/src/gc/rc_heap_tests.rs` | NEW | RcMagrGC 单元测试 |
| `src/runtime/src/gc/README.md` | NEW | gc/ 目录 README |
| `src/runtime/src/vm_context.rs` | MODIFY | 新增 `heap` 字段 + `heap()` accessor |
| `src/runtime/src/vm_context_tests.rs` | MODIFY | heap accessor 测试 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | ArrayNew / ArrayNewLit / ObjNew 走 ctx.heap() |
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | jit_array_new / jit_array_new_lit / jit_obj_new 走 vm_ctx.heap() |
| `docs/design/vm-architecture.md` | MODIFY | 新增 "GC 子系统" 段 + 多阶段路线图 |
| `docs/roadmap.md` | MODIFY | L2 M7 新增 MagrGC 接口完成项 |

**只读引用**：

- `src/runtime/src/metadata/types.rs` — 理解 `Value` / `ScriptObject` / `NativeData` 形状
- `src/runtime/src/jit/helpers.rs` — `vm_ctx_ref` ABI 模式
- `src/runtime/src/jit/frame.rs` — `JitModuleCtx::vm_ctx` 字段

## Out of Scope

- Phase 2：环检测真实算法（dumpster 2.0 集成 / 自研 Bacon-Rajan 二选一）
- Phase 3：Mark-Sweep 实现、`Value::Array/Map/Object` 改用 `GcRef<T>`、Cranelift stack maps
- Phase 4+：分代 / 并发 / MMTk 集成
- **Phase 1.5（独立 spec）**：`corelib/object.rs:34` + `corelib/fs.rs:51` + `corelib/tests.rs` 中的 `Rc::new(RefCell::new(...))` 迁移 —— 需要先扩展 `NativeFn` 签名带 `&VmContext`，超出本次 Phase 1 接口收口范围
- 字符串移到 z42 脚本实现（依赖 Phase 2/3 成熟的 GC，本次仅记录为后续动机）
- `Value::Map` callsite 迁移：grep 未发现脚本驱动的 Map 分配点；接口提供 `alloc_map()` 占位

## Open Questions

（已全部裁决，无遗留）
