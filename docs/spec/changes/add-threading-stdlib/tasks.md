# Tasks: add Std.Threading stdlib

> 状态：🟡 进行中 | 创建：2026-05-20 | 类型：vm + feat

## 进度概览
- [ ] 阶段 1: VmCore.module + Vm 瘦身
- [ ] 阶段 2: VmCore.threads registry
- [ ] 阶段 3: `VmContext::new_with_core` 构造函数
- [ ] 阶段 4: `__thread_spawn` / `__thread_join` native fns
- [ ] 阶段 5: z42.threading 包 + Thread / ThreadException 类
- [ ] 阶段 6: 4 个 stdlib tests
- [ ] 阶段 7: 文档同步
- [ ] 阶段 8: 归档 + commit

## 阶段 1: VmCore.module + Vm 瘦身

- [ ] 1.1 `src/runtime/src/vm_context.rs` VmCore 加 `module: Arc<Module>` 字段
- [ ] 1.2 `VmContext::new()` 签名变 `pub fn new(module: Module) -> Pin<Box<Self>>`
- [ ] 1.3 `src/runtime/src/vm.rs` `Vm` 结构体去 `module` 字段；`Vm::new(mode)` 不再 take module；`Vm::run` 从 `ctx.core.module` 取 module
- [ ] 1.4 grep 所有 `Vm::new(` callsite，调整签名；所有 `VmContext::new()` callsite 加 module 参数
- [ ] 1.5 cargo build (dev + release) GREEN
- [ ] 1.6 ./scripts/test-stdlib.sh 不回归

## 阶段 2: VmCore.threads registry

- [ ] 2.1 VmCore 加 `threads: Mutex<HashMap<u64, JoinHandle<Result<(), anyhow::Error>>>>` + `next_thread_id: AtomicU64`
- [ ] 2.2 构造路径初始化两个字段
- [ ] 2.3 cargo build GREEN

## 阶段 3: VmContext::new_with_core 构造函数

- [ ] 3.1 `pub fn new_with_core(core: Arc<VmCore>) -> Pin<Box<Self>>` — 不构造新 VmCore，共享传入的；构造 VmContext + register self；scanner 已 walk registry 所以不需要新装 scanner
- [ ] 3.2 单测 `vm_context_tests.rs` 加 `vm_context_new_with_core_shares_core`：构两个 VmContext 共享 VmCore，verify `core.vm_contexts.lock().len() == 2`
- [ ] 3.3 cargo test 通过

## 阶段 4: native fns

- [ ] 4.1 `src/runtime/src/corelib/threading.rs` NEW —— 实现 `builtin_thread_spawn` / `builtin_thread_join`
- [ ] 4.2 `src/runtime/src/corelib/mod.rs` 注册两个 builtin（key: `__thread_spawn` / `__thread_join`）
- [ ] 4.3 `src/runtime/src/corelib/threading_tests.rs` NEW —— Rust 直接测试 `__thread_spawn` 路径
- [ ] 4.4 `src/runtime/tests/cross_thread_smoke.rs` 加 `spawn_via_builtin_then_join` 集成测试
- [ ] 4.5 cargo test 全过

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

（实施中发现写这里）

—

## 后续相关 spec（依赖顺序）

| 名称 | 范围 | 依赖 |
|------|------|------|
| `add-sync-primitives` | `Std.Threading.Mutex<T>` / `Channel<T>` 用户类型 | 本 spec |
| `add-gc-safepoint` | interp + JIT safepoint，让 GC 能安全 stop-the-world | add-vmcontext-registry |
| `add-concurrent-gc` | mark-sweep 升级到并发（Phase A 性能轨道）| `add-gc-safepoint` |
| `add-spawn-syntax` | `spawn` / `task scope` 语言层（L3，concurrency.md §3.5） | 本 spec |
