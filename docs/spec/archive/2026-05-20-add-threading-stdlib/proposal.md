# Proposal: add Std.Threading stdlib — first user-visible threading API

## Why

`add-multithreading-foundation`（archived 2026-05-20）+ `add-vmcontext-registry`（archived 2026-05-20）已把 z42 VM 类型层做到 Send+Sync、VmCore 注册表能扫所有 VmContext frame —— 但**用户脚本无法启动线程**。当前 stdlib 没有任何 thread API；roadmap z42.threading 列在"未来包"未实施。

落地最小 `Std.Threading.Thread.Start(action) → Thread` + `Thread.Join() → void` 是 multi-threading 落地的**第一个用户可见里程碑**。一旦 ship：
- 用户可以 `var t = Thread.Start(() => { ... }); t.Join();`
- 跨线程共享 GC 对象自动 work（Send+Sync 已就位）
- 后续 `Std.Threading.Mutex<T>` / `Channel<T>` 直接 build on top
- async/await（L3）落地时把 task scheduler 接到这个 thread API 上

**Why not later**：threading stdlib 在 stdlib roadmap 里早就排在 P0/P1（z42 build-driver 自举需要 thread pool 跑测试 / 并行打包等场景）。VM 基础已稳；继续推迟没有 architectural 收益。

**主要 unknowns**：
- GC 并发安全：mark-sweep 还不是真并发；多线程下要靠 Mutex 锁住 heap 操作来串行化。性能差但正确。`add-gc-safepoint` + `add-concurrent-gc` 改进性能但本 spec 不阻塞
- 异常跨线程：spawned action throw 后 `Thread.Join` 如何 surface？C# 模式 wrap 进 `ThreadException`

## What Changes

**Architectural**：
- VmCore 加 `module: Arc<crate::metadata::Module>` 字段 —— 主程序 Module 移入 VmCore 共享。Vm 结构体瘦身，主要持 `ExecMode` 等运行配置
- 新增 `VmContext::new_with_core(Arc<VmCore>) -> Pin<Box<VmContext>>` 构造函数 —— 共享 VmCore 跨线程
- VmCore 加 `threads: Mutex<HashMap<u64, std::thread::JoinHandle<Result<(), anyhow::Error>>>>` —— 线程注册表（同 processes 模式）
- VmCore 加 `next_thread_id: AtomicU64` —— 单调递增 slot id

**VM 内部**：
- 新 native fn `__thread_spawn(action: Value) -> i64`：take FuncRef / Closure，spawn 一个 std::thread，新线程构造自己的 VmContext + run action via interp，返 slot id
- 新 native fn `__thread_join(slot_id: i64) -> void`：take JoinHandle by slot id，调 .join()，OK → return；Err → throw `ThreadException`
- 跨线程 panic catch：spawn 内 catch_unwind，转译为 `anyhow::Error`

**Stdlib z42.threading 新包**：
- 新建 `src/libraries/z42.threading/` 包，namespace `Std.Threading`
- `Thread.z42`：`public class Thread { ... }` 持 i64 slot id
- `Thread.Start(action: Action) → Thread` static factory
- `Thread.Join() → void` instance method
- `ThreadException.z42`：`namespace Std;` 抛出的异常类型
- workspace.toml + build-stdlib.sh index 注册

**Tests**：
- `tests/thread_basic.z42`：spawn → join
- `tests/thread_shared_static.z42`：spawn writes, main reads
- `tests/thread_throws.z42`：action throws → Join 抛 ThreadException
- `tests/thread_many.z42`：spawn 10 threads，join all

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|---|---|---|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `module: Arc<Module>` / `threads: Mutex<HashMap>` / `next_thread_id: AtomicU64`；新 `VmContext::new_with_core` 构造函数 |
| `src/runtime/src/vm.rs` | MODIFY | Vm 改为持 `Arc<Module>`（拷贝），通过 VmCore 共享；构造调整 |
| `src/runtime/src/main.rs` | MODIFY | 启动构造 VmCore 时塞入 module |
| `src/runtime/src/host/ops.rs` / `state.rs` | MODIFY | host embedding 路径同步 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册新 `__thread_spawn` / `__thread_join` builtin |
| `src/runtime/src/corelib/threading.rs` | NEW | 实现两个 builtin |
| `src/runtime/src/corelib/threading_tests.rs` | NEW | 单元 + 集成测试 |
| `src/libraries/z42.threading/z42.threading.z42.toml` | NEW | manifest |
| `src/libraries/z42.threading/src/Thread.z42` | NEW | 用户类 |
| `src/libraries/z42.threading/src/ThreadException.z42` | NEW | 异常类（namespace Std） |
| `src/libraries/z42.threading/tests/thread_basic.z42` | NEW | spawn + join |
| `src/libraries/z42.threading/tests/thread_shared_static.z42` | NEW | shared state |
| `src/libraries/z42.threading/tests/thread_throws.z42` | NEW | exception propagation |
| `src/libraries/z42.threading/tests/thread_many.z42` | NEW | N threads |
| `src/libraries/z42.workspace.toml` | MODIFY | 加 z42.threading member |
| `scripts/build-stdlib.sh` | MODIFY | LIBS array + index.json 加 z42.threading |
| `docs/design/stdlib/organization.md` | MODIFY | 现状表加 z42.threading 行 |
| `docs/design/stdlib/overview.md` | MODIFY | Module Catalog 加 z42.threading 行 |
| `docs/design/stdlib/roadmap.md` | MODIFY | 把 z42.threading 从未来包移到已落地 |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 表 "用户层线程 API" 行 ❌ → ✅；移除 add-threading-stdlib 待办 |

只读引用：
- `docs/spec/archive/2026-05-20-add-vmcontext-registry/` —— 注册表 prerequisite

## Out of Scope

- **`Std.Threading.Mutex<T>` / `Channel<T>` 用户类型** —— 下个 spec `add-sync-primitives`
- **GC safepoint / 并发收集** —— `add-gc-safepoint` / `add-concurrent-gc`
- **`spawn` 语言语法** —— L3，`add-spawn-syntax`
- **Thread.Interrupt / Abort** —— 设计上 z42 不引入 abort 模型；用户用 channel signal
- **Thread name / id retrieval**（`Thread.CurrentId` / `Name`）—— v0 不必，可后续 add
- **Timeout on Join**（`TryJoin(TimeSpan)` 等）—— 同上
- **Per-thread heap-local 存储**（thread_local 模拟）—— 同上，初始不必
- **JIT 路径的 spawn 支持** —— JIT extern-C 复杂度大，本 spec 仅 interp 路径；JIT 模块跑 spawned action 走 fallback interp（如有需要单独 spec）

## Open Questions

- [ ] **Action 类型**：take `Value::Closure` / `Value::FuncRef`？还是统一 `Action`（z42.core 中的 0-arity delegate）？倾向用 `Action`（公开 contract 清晰）；native fn 内部接受任意 callable Value
- [ ] **Thread.Join 重复调用**：第二次 Join 同一 Thread 抛 `ThreadException("already joined")` 还是 silently no-op？倾向抛（C# 也是抛）
- [ ] **GC 性能**：跨线程下 GC mark 通过 Mutex 串行化，contention 可能拖慢。基准测试在 design.md Decision 3 详
- [ ] **`Vm::module` → `VmCore::module` 迁移**：是否要保留 `Vm.module` 作为冗余指针 (`Arc::clone(&core.module)`)？省一次间接，但 invariant 复杂。倾向**纯走 VmCore.module**（去重）
