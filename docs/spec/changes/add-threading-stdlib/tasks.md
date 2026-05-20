# Tasks: add Std.Threading stdlib

> 状态：🟡 进行中 | 创建：2026-05-20 | 类型：vm + feat

## 进度概览
- [x] 阶段 1: VmCore.module + Vm 瘦身
- [x] 阶段 2: VmCore.threads registry
- [x] 阶段 3: `VmContext::new_with_core` 构造函数
- [x] 阶段 4: `__thread_spawn` / `__thread_join` native fns
- [ ] 阶段 5: z42.threading 包 + Thread / ThreadException 类
- [ ] 阶段 6: 4 个 stdlib tests
- [ ] 阶段 7: 文档同步
- [ ] 阶段 8: 归档 + commit

## 阶段 1: VmCore.module + Vm 瘦身

- [x] 1.1 `src/runtime/src/vm_context.rs` VmCore 加 `module: Arc<Module>` 字段 — 实际选 `Option<Arc<Module>>`，详见备注
- [x] 1.2 `VmContext::new()` 签名变 `pub fn new(module: Module) -> Pin<Box<Self>>` — 实际拆成 `new()` (test) + `with_module(Module)` (prod)
- [x] 1.3 `src/runtime/src/vm.rs` `Vm` 结构体去 `module` 字段；`Vm::new(mode)` 不再 take module；`Vm::run` 从 `ctx.core.module` 取 module
- [x] 1.4 grep 所有 `Vm::new(` callsite，调整签名；所有 `VmContext::new()` callsite 加 module 参数
- [x] 1.5 cargo build (dev + release) GREEN
- [x] 1.6 ./scripts/test-stdlib.sh 不回归

## 阶段 2: VmCore.threads registry

- [x] 2.1 VmCore 加 `threads: Mutex<HashMap<u64, JoinHandle<Result<(), anyhow::Error>>>>` + `next_thread_id: AtomicU64`
- [x] 2.2 构造路径初始化两个字段
- [x] 2.3 cargo build GREEN

## 阶段 3: VmContext::new_with_core 构造函数

- [x] 3.1 `pub fn new_with_core(core: Arc<VmCore>) -> Pin<Box<Self>>` — 不构造新 VmCore，共享传入的；构造 VmContext + register self；scanner 已 walk registry 所以不需要新装 scanner
- [x] 3.2 单测 `vm_context_tests.rs` 加 `vm_context_new_with_core_shares_core`：构两个 VmContext 共享 VmCore，verify `core.vm_contexts.lock().len() == 2`（实际加了 3 个相关单测：shares_core / drop_only_removes_self / shares_static_fields）
- [x] 3.3 cargo test 通过

## 阶段 4: native fns

- [x] 4.1 `src/runtime/src/corelib/threading.rs` NEW —— 实现 `builtin_thread_spawn` / `builtin_thread_join`；`__thread_join` 走 discriminator-array 协议（同 process），让 z42 facade 把 1=throw / 2=already-joined 转 ThreadException
- [x] 4.2 `src/runtime/src/corelib/mod.rs` 注册两个 builtin（key: `__thread_spawn` / `__thread_join`），追加到 BUILTINS 末尾保留既有 BuiltinId 稳定
- [x] 4.3 `src/runtime/src/corelib/threading_tests.rs` NEW —— 8 个 Rust 单测覆盖参数校验 + 未知 slot 路径
- [x] 4.4 `src/runtime/tests/cross_thread_smoke.rs` 加 `spawn_via_builtin_then_join_runs_action_on_worker_thread` 端到端集成测试（构造 minimal void-action Module + spawn + join）
- [x] 4.5 cargo test 全过 — 420 unit + 4 cross_thread + 其余集成；stdlib 62/62 不回归

## 阶段 5: z42.threading 包

- [ ] 5.1 `src/libraries/z42.threading/z42.threading.z42.toml` NEW —— manifest
- [ ] 5.2 `src/libraries/z42.threading/src/Thread.z42` NEW —— 用户类（持 `_slot: long`；Start factory；Join method）
- [ ] 5.3 `src/libraries/z42.threading/src/ThreadException.z42` NEW —— namespace Std；继承 Exception
- [ ] 5.4 `src/libraries/z42.workspace.toml` 加 `z42.threading` member
- [ ] 5.5 `scripts/build-stdlib.sh` LIBS array + index.json 加 `Std.Threading`

## 阶段 6: stdlib tests

- [ ] 6.1 `tests/thread_basic.z42` —— spawn empty + join
- [ ] 6.2 `tests/thread_shared_static.z42` —— spawn 写 static field；main join + read
- [ ] 6.3 `tests/thread_throws.z42` —— spawn throw；Join 抛 ThreadException
- [ ] 6.4 `tests/thread_many.z42` —— spawn 10 threads，全 join
- [ ] 6.5 ./scripts/test-stdlib.sh z42.threading GREEN
- [ ] 6.6 ./scripts/test-stdlib.sh 全量 66/66 不回归

## 阶段 7: 文档同步

- [ ] 7.1 `docs/design/stdlib/organization.md` 现状表加 `z42.threading` 行
- [ ] 7.2 `docs/design/stdlib/overview.md` Module Auto-load Policy 表加 `z42.threading` → `Std.Threading` 行
- [ ] 7.3 `docs/design/stdlib/roadmap.md` 把 z42.threading 从"未来包"移到"已落地"
- [ ] 7.4 `docs/design/runtime/concurrency.md` "Runtime foundation 现状" 表 "用户层线程 API" 行 ❌ → ✅；后续 spec 列表 add-threading-stdlib 标 ✅
- [ ] 7.5 `docs/design/runtime/vm-architecture.md` VmContext 章节增 `new(module)` / `new_with_core` 双 entry 说明

## 阶段 8: 归档 + commit

- [ ] 8.1 mv → `docs/spec/archive/2026-05-20-add-threading-stdlib/`
- [ ] 8.2 commit + push（建议分 commit：阶段 1+2+3，阶段 4，阶段 5+6+7+8）
- [ ] 8.3 verify CI GREEN

## 备注

### Phase 1 实施期记录（commit 31e829ee 后）

- **VmCore.module 选择 `Option<Arc<Module>>` 而非 `Arc<Module>`**：因为 cargo unit tests（vm_context_tests / heap tests / corelib 单测共 15+ 处 `VmContext::new()`）不需要真实 Module。设计 doc Decision 1 原方案要求所有路径都 pass module；改为 Option 后 test 路径走 `VmContext::new()`（None），prod 路径走 `VmContext::with_module(module)`（Some）。`__thread_spawn` 在没有 module 的 ctx 上 bail!，逻辑兼容。

### Phase 4 实施期记录

- **`__thread_join` 返回 discriminator array** 而不是裸 `bail!` —— 跟 `__process_run` / `__process_spawn` 模式一致，让 z42-side `Thread.Join()` 把 1=throw / 2=already-joined 转 `Std.ThreadException`，错误流走 z42 catch path 而不是 anyhow → 进程级 panic。
- **stack closure 拒绝跨线程**：`Value::StackClosure { env_idx }` 的 env 在 caller frame arena，spawn 一刻 caller frame 仍存活，但 caller return 后 env 失效；worker 异步继续会 use-after-free。实施期决定直接 bail!（编译器若知道 closure escape 应 promote 到 heap Closure）。

—

## 后续相关 spec（依赖顺序）

| 名称 | 范围 | 依赖 |
|------|------|------|
| `add-sync-primitives` | `Std.Threading.Mutex<T>` / `Channel<T>` 用户类型 | 本 spec |
| `add-gc-safepoint` | interp + JIT safepoint，让 GC 能安全 stop-the-world | add-vmcontext-registry |
| `add-concurrent-gc` | mark-sweep 升级到并发（Phase A 性能轨道）| `add-gc-safepoint` |
| `add-spawn-syntax` | `spawn` / `task scope` 语言层（L3，concurrency.md §3.5） | 本 spec |
