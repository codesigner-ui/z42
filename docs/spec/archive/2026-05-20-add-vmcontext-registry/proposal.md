# Proposal: add VmContext registry for cross-thread GC root scanning

## Why

`add-multithreading-foundation`（2026-05-20 archived）落地后，z42 VM 类型层已满足 `Send + Sync`，**但仍受单 VmContext per VmCore 不变量约束**：GC scanner closure 通过捕获 *单一* VmContext 的 per-thread `Arc<Mutex<...>>` clone 看到 `pending_exception` / `call_stack` / `func_ref_slots`。

这个不变量是 add-multithreading-foundation Phase 2.2 实施期发现的（design.md Decision 8 + tasks.md 备注详记）。Multi-threading 实际要做的事是：**每个 OS 线程一个 VmContext**，每个有自己的 frame stack。GC scanner 必须看见**所有线程**的 frames 才能不漏扫 root，否则跨线程对象误回收。

本 spec 解决这一不变量：

- VmCore 加 VmContext 注册表
- VmContext 构造时注册自己；Drop 反注册
- GC scanner 改为 walk 注册表，对每个活的 VmContext 扫 per-thread state
- VmContext 地址稳定（构造后不可 move）—— 通过 `Pin<Box<VmContext>>` 强制

**为什么必须先于 `add-threading-stdlib`**：threading stdlib 第一行 native fn `__thread_spawn(action)` 就会 `let ctx = VmContext::new()`，把这第二个 VmContext 投到一个新 thread 上跑。如果 VmCore.heap 的 scanner 看不见这个 VmContext 的 frames，新线程上脚本运行期触发 GC → 跨线程对象误清 → 用户脚本运行到一半值变 Null。Threading stdlib 没法 ship 就建立在这个前提上。

**实施风险**：API 改动 `VmContext::new()` 返回 `Pin<Box<VmContext>>` 而非 `VmContext`，触及编译器 / VM 入口的所有 `let mut ctx = VmContext::new();` 调用点（grep ~10 个）。属机械工作，但是真实的 API 边界变更。

## What Changes

- **`VmCore` 加 registry 字段**：`pub(crate) vm_contexts: Mutex<Vec<VmContextPtr>>`，其中 `VmContextPtr` 是 `*const VmContext` 的 transparent Send/Sync wrapper（带 SAFETY 注释）
- **VmContext 构造路径切到 `Pin<Box<VmContext>>`**：`pub fn new() -> Pin<Box<VmContext>>`，内部 `_pin: PhantomPinned` 防 move
- **VmContext::new 在 boxed/pinned 后注册自己**：把稳定地址 push 进 `core.vm_contexts`
- **VmContext::Drop 反注册**：从注册表 retain 掉自己的 ptr
- **GC scanner closure 改写**：上锁 registry → 遍历每个 ptr → `unsafe { &*ptr }` 取 VmContext → 扫其 `pending_exception` / `call_stack` / `func_ref_slots`
- **`&mut VmContext` 参数 → `&VmContext`**：4 个 callsite（`vm.rs::Vm::run` / `jit/mod.rs` 3 个），都是历史风格（实际所有 ctx 方法都是 `&self`）
- **scanner closure 不再捕获 per-thread Arc 克隆**：单 VmContext 优化路径删除；所有线程都走 registry

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|---|---|---|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 vm_contexts 字段；VmContext 加 _pin 字段；`new()` 返 `Pin<Box<Self>>`；构造路径重写注册；新 Drop impl 反注册；scanner closure 改 walk registry；移除 per-thread Arc 捕获 |
| `src/runtime/src/vm.rs` | MODIFY | `Vm::run` 参数 `&mut VmContext` → `&VmContext` |
| `src/runtime/src/jit/mod.rs` | MODIFY | `JitModule::run` / `run_fn` / 顶层 `run` 三处 `&mut VmContext` → `&VmContext` |
| `src/runtime/src/main.rs` | MODIFY | VM 启动构造 `let mut ctx = VmContext::new();` → `let ctx = VmContext::new();` |
| `src/runtime/src/host/ops.rs` | MODIFY | host embedding `build_host_module` / `build_session_module` 适配 Pin<Box<VmContext>> 持有 |
| `src/runtime/src/host/state.rs` | MODIFY | `HostModule.ctx: VmContext` → `Pin<Box<VmContext>>` |
| `src/runtime/src/host/mod.rs` | MODIFY | 同 ops.rs（构造路径） |
| `src/toolchain/test-runner/src/bootstrap.rs` | MODIFY | test runner 构造路径 |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 添加新测试 `multi_vm_contexts_alloc_and_collect`：在多线程内各构造自己的 VmContext，触发 GC，验证 frames 都被 walk |
| `src/runtime/src/gc/arc_heap_tests/send_sync.rs` | MODIFY | 加 `assert_send_sync::<VmContextPtr>()`（虽然 unsafe impl，仍想编译期钉死） |
| `docs/design/runtime/vm-architecture.md` | MODIFY | "VmContext / VmCore" 章节加 registry 描述；删除"single-VmContext-per-VmCore 不变量"段（已解） |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 表更新："多 VmContext 共享 GC heap" 由 ❌ → ✅；移除 single-invariant 行 |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 不动（无新延后）；本 spec 是 closing 一个 deferred |

只读引用：
- `docs/spec/archive/2026-05-20-add-multithreading-foundation/` —— 历史决策来源
- `src/runtime/src/gc/refs.rs` —— GcRef 不变

## Out of Scope

- **`Std.Threading.Thread.Start` 用户面 API** —— 下个 spec `add-threading-stdlib`
- **GC safepoint** —— 单独 spec `add-gc-safepoint`；当前 GC 仍是 stop-the-world-on-collect-call，没有 safepoint 协议
- **真并发 GC** —— Phase A 性能轨道
- **VmContext clone** —— VmContext 不 Clone；每个线程拿自己的 `Pin<Box<VmContext>>`，共享 VmCore via Arc

## Open Questions

- [ ] **VmContextPtr 用 `*const` 还是 `usize`**？`*const VmContext` 直接但 Rust 默认 !Send；`usize` storage 自动 Send/Sync 但 deref 需要再 cast。倾向 `*const VmContext` + `unsafe impl Send/Sync`（同 VmFrame 模式，design.md Decision 1 详）
- [ ] **`Pin<Box<VmContext>>` 还是 `Box<VmContext>` 没 Pin**？后者足够地址稳定（Box 已堆分配），Pin 主要防 `*box = other` 这种 move-out。pre-1.0 倾向 `Pin<Box>` 强约束（PhantomPinned 显式标 !Unpin）；运行时无开销
- [ ] **GC scanner 持锁 vs snapshot registry**？持锁防 race，但 alloc 路径触发 GC 时也走 alloc → mutator 线程→ heap.alloc → 可能 trigger collect → scanner → lock registry。同一线程 VmContext::new 时也 lock。如果在 alloc 内 lock registry 同 mutex 即 deadlock。需要确认 registry mutex 跟 new() 注册 mutex 是同一个 → 设计上是的，所以这是真实风险。倾向：scanner clone 整个 Vec 出来再 release lock，loop time 短
