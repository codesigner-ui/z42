# Proposal: add multi-threading foundation (runtime structural prep)

## Why

z42 当前 VM 单线程：[`VmContext`](../../../../src/runtime/src/vm_context.rs#L72) 11 个字段全是 `Rc<RefCell<T>>`，[`GcRef<T>`](../../../../src/runtime/src/gc/refs.rs#L50) backing 是 `Rc<GcAllocation<T>>`，[`MagrGC`](../../../../src/runtime/src/gc/heap.rs#L55) trait 无 `Send/Sync` 边界。任何 `Std.Threading.Thread.Start(action)` 形态的 stdlib API 都过不了编译——Value 跨线程 Send 会被 trait bound 拒。

**roadmap 0.8.x 多线程 + async/await 是终态**，但前置阻塞远早于那个 minor：每个新加的 stdlib lib（z42.regex / z42.crypto / 等）只要新增 VmContext 字段就在加深"单线程化"债务。**越晚做底座 refactor 越贵**，而底座 refactor 本身不引入用户可见行为，是纯 internal prep。

本 spec 做的是**结构性 prep**——把 VmContext 的"per-thread vs 共享"字段切干净 + 升级 GcRef backing 到 Arc + 给 MagrGC trait 加 Send/Sync 边界。完成后：
- 单线程行为 100% 不变（所有 stdlib / VM 测试照绿）
- 多线程 stdlib API（`Std.Threading.Thread` / `Mutex` / `Channel`）可在**下一份 spec** 直接落地
- 并发 GC（Phase A 性能轨道）可在**再下一份 spec** 设计 safepoint

**反例（如果不做本 spec）**：直接开 stdlib threading spec → 第一条 native fn `thread_spawn(action_fn)` 编译失败，连环退回到 VmContext 改造 → spec scope 爆炸。

## What Changes

**总览**（分三块）：

1. **VmContext 切分**：引入 `VmCore`（跨线程共享状态）+ `VmContext`（per-thread 视图）
   - 共享：`static_fields` / `static_field_index` / `type_registry`（在 Module 里）/ `lazy_loader` / `native_types` / `native_libs` / `pinned_owned_buffers` / `processes` / GC backend
   - per-thread：`call_stack` / `pending_exception` / `func_ref_slots`（call-site 闭包句柄）/ frame guard 链
   - VmContext 持 `Arc<VmCore>`；per-thread 字段仍可 `Rc<RefCell<T>>`（单线程内不需要 Sync）

2. **GcRef backing 升 Arc**：[`GcRef<T>`](../../../../src/runtime/src/gc/refs.rs#L50) 内部 `Rc<GcAllocation<T>>` → `Arc<GcAllocation<T>>`；`GcAllocation.inner: RefCell<T>` → `Mutex<T>`（或 `parking_lot::Mutex` 见 design 决策）
   - `GcRef::borrow()` / `borrow_mut()` API 形态保留；语义从 RefCell 借用规则切到 Mutex lock（panic-on-recursive-lock 等价 RefCell 借用 panic）
   - Phase 3e 的 `GcAllocation` finalizer Drop hook 保留语义

3. **MagrGC trait + RcMagrGC → ArcMagrGC**：[`MagrGC`](../../../../src/runtime/src/gc/heap.rs#L55) trait 加 `Send + Sync` 边界；`RcMagrGC` rename / 替换为 `ArcMagrGC`（backing 自然换 Arc）；内部 `Rc<RefCell<HashMap>>` registry 切到 `Arc<Mutex<HashMap>>`

**Out of Scope**（明确写出避免实施期 scope 爆炸）：

- **任何用户可见线程 API**（`Std.Threading.Thread.Start` 等）→ 下一份 spec `add-threading-stdlib`
- **`spawn` / `task scope` 语言语法** → L3 阶段，concurrency.md 已规划
- **并发 GC / safepoint 插入** → 单独 spec，Phase A 性能轨道
- **`Send` / `Sync` 作为 z42 语言 interface** → concurrency.md §4 已规划，L3 阶段
- **JIT 路径的 Send 化** → JIT 当前 feature-flag 后置；先 interp 闭环
- **VmContext 字段重命名** → 只动可见性 / wrap 类型，不动名字（保 git blame）

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | 新增 `VmCore`；VmContext 持 `Arc<VmCore>`；按字段类型迁移 |
| `src/runtime/src/gc/refs.rs` | MODIFY | `GcRef` / `GcAllocation` backing：Rc→Arc；RefCell→Mutex |
| `src/runtime/src/gc/heap.rs` | MODIFY | `MagrGC` trait 加 `Send + Sync` 边界 |
| `src/runtime/src/gc/rc_heap.rs` | MODIFY | RcMagrGC → ArcMagrGC（内部 Rc→Arc，RefCell→Mutex）；保留文件名作为兼容 alias 可选 |
| `src/runtime/src/gc/mod.rs` | MODIFY | re-export 调整；Phase 表加一行（Phase 4a：Send-safe foundation）|
| `src/runtime/src/gc/types.rs` | MODIFY | 任何 `Send` / `Sync` 边界相关的 callback type alias 加 bound |
| `src/runtime/src/gc/heap_tests.rs` | MODIFY | Send/Sync assertion 单测（trait-bound 编译期校验）|
| `src/runtime/src/interp/mod.rs` | MODIFY | 任何借 ctx 字段处更新调用方式（多半 ctx.core.field.lock() 替换 ctx.field.borrow()）|
| `src/runtime/src/interp/exec_*.rs` | MODIFY | 同上 |
| `src/runtime/src/jit/mod.rs` | MODIFY | 同上（feature-gated 路径） |
| `src/runtime/src/host/*.rs` | MODIFY | host embedding 接口也走 ctx，需要同步 |
| `src/runtime/src/corelib/*.rs` | MODIFY | 所有 corelib NativeFn 签名是 `&VmContext`，访问字段方式变化 |
| `src/runtime/src/exception/mod.rs` | MODIFY | call_stack / VmFrame 路径 |
| `src/runtime/src/main.rs` | MODIFY | VM 启动构造路径 |
| `src/runtime/src/lib.rs` | MODIFY | re-export 调整 |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 新增 "VmCore / VmContext 分离" 章节；GC Phase 4a 行 |
| `docs/design/runtime/concurrency.md` | MODIFY | 增 "runtime foundation 现状" 章节，链接本 spec archive |

**只读引用**（理解但不改）：
- `docs/design/runtime/gc-handle.md` —— GcRef 设计契约
- `Cargo.toml` —— 已含 `parking_lot` 否则添依赖（见 design.md Decision 3）

预估改动面：**runtime 侧 ~30 个文件，1500-2500 行**（大半是 `.borrow()` / `.borrow_mut()` → `.lock()` 的机械替换）

## Out of Scope

如 What Changes 段已列。本 spec 仅做"代码 Send-safe"，**不引入任何线程的 spawn / join / 同步原语**。spec 完成后，run 时仍是单线程；只是 VM 类型已经允许 `&VmContext` 跨线程共享。

## Open Questions

- [ ] **Mutex 选型**：`std::sync::Mutex` 还是 `parking_lot::Mutex`？后者更快（无 poisoning）但加依赖。倾向 parking_lot；在 design.md Decision 3 确定
- [ ] **VmContext per-thread 字段是否保留 Rc/RefCell**？方案 A：保留（单线程内更便宜，跨线程要主动 clone Arc<VmCore>）。方案 B：全 Arc/Mutex（一致但有原子开销）。倾向 A；在 design.md Decision 2 确定
- [ ] **RcMagrGC 是 rename 还是新增 ArcMagrGC**？rename = 破坏调用方但更干净；新增 = 双 backend 共存（一段时间）但维护成本翻倍。倾向 rename（pre-1.0，按 philosophy.md "不为破坏性顾虑而牺牲最佳方案"）；在 design.md Decision 1 确定
- [ ] **基线性能影响**：Arc::clone 的原子 op 在 GC 热路径（refcount on every Value clone）的开销？需要 design.md 给出**估计** + 后续 spec 跑 benchmark 验证
