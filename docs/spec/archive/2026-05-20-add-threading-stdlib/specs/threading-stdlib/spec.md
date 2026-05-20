# Spec: Std.Threading stdlib — Thread.Start / Thread.Join

## ADDED Requirements

### Requirement: Thread spawn & join

#### Scenario: Basic spawn-then-join
- **WHEN** user script runs:
  ```z42
  using Std.Threading;
  void Main() {
      var t = Thread.Start(() => { /* empty */ });
      t.Join();
      Console.WriteLine("done");
  }
  ```
- **THEN** output is `done`
- **AND** no error / exception is raised

#### Scenario: Spawned action writes a shared static; main thread reads after join
- **GIVEN** `class Shared { public static int Counter = 0; }`
- **WHEN** spawned action runs `Shared.Counter = 42;`
- **AND** main thread `t.Join();` returns
- **AND** main thread reads `Shared.Counter`
- **THEN** value is 42
- **AND** the read sees the write (happens-before guaranteed by Join sync)

#### Scenario: Spawned action throws → Thread.Join rethrows as ThreadException
- **GIVEN** spawned action body `throw new Exception("boom");`
- **WHEN** main thread calls `t.Join()`
- **THEN** `t.Join()` throws a `ThreadException`
- **AND** the `ThreadException.Message` contains `"boom"` (inner exception message)

#### Scenario: 10 concurrent threads each increment a shared counter
- **GIVEN** `class C { public static int N = 0; }`
- **AND** 10 threads each running `for (int i = 0; i < 100; i++) C.N = C.N + 1;`
- **WHEN** all `Join()` returns
- **THEN** `C.N` is somewhere in `[100, 1000]` (race-y by design — proves no crash, NOT correctness of unsynchronized update)
- **AND** no thread panics or leaks

### Requirement: Thread.Join contract

#### Scenario: Double-join throws
- **GIVEN** `var t = Thread.Start(action); t.Join();`
- **WHEN** `t.Join()` called second time
- **THEN** `ThreadException` thrown with message `"thread already joined"`

#### Scenario: Joining a never-started Thread is impossible
- **WHEN** user tries to construct `new Thread()` directly (no `Start`)
- **THEN** compile error (constructor is private; only `Thread.Start(Action)` factory)

### Requirement: VmContext lifecycle on spawned thread

#### Scenario: Spawned thread auto-registers in VmCore
- **GIVEN** spawning thread T0 with `VmCore.vm_contexts` length 1
- **WHEN** Thread.Start(action) is called
- **AND** the new OS thread T1 has constructed its `Pin<Box<VmContext>>` via
  `VmContext::new_with_core(Arc::clone(&core))`
- **THEN** `VmCore.vm_contexts` length is 2 (both T0 and T1 registered)

#### Scenario: Spawned thread auto-deregisters on completion
- **GIVEN** Thread T1 finishes its action
- **WHEN** T1's VmContext drops (end of spawn closure)
- **THEN** `VmCore.vm_contexts` removes T1's entry
- **AND** length is 1 again (only T0 remains)

#### Scenario: GC scanner walks both threads' frames
- **GIVEN** T0 main thread + T1 spawned thread, both in middle of script
- **WHEN** GC `collect_cycles()` invoked from any thread
- **THEN** the registered external root scanner walks both T0 and T1's
  `call_stack` frames under `vm_contexts.lock()`
- **AND** no live object reachable from either thread's frames is collected

### Requirement: Native fn surface

#### Scenario: `__thread_spawn` builtin signature
- **WHEN** the corelib registers builtins
- **THEN** `__thread_spawn(action: Value) -> Value` is callable from `Thread.Start`
- **AND** parameter must be a callable Value (Closure / FuncRef / Action delegate); invalid type → throw `ThreadException("__thread_spawn requires Action; got <kind>")`
- **AND** return value is `Value::I64(slot_id)` (i64)

#### Scenario: `__thread_join` builtin signature
- **WHEN** Thread.Join calls `__thread_join(this._slot)`
- **THEN** native fn locates JoinHandle in `VmCore.threads`, takes it out, calls `JoinHandle::join()`
- **AND** OK → returns `Value::Null`
- **AND** Err (action panicked / threw) → throws `ThreadException` with inner message
- **AND** missing slot id (already joined or invalid) → throws `ThreadException("thread already joined")`

### Requirement: 单线程不回归

#### Scenario: stdlib + VM e2e + cargo unit 全绿
- **WHEN** GREEN gauntlet runs
- **THEN** stdlib 62 (+4 z42.threading) = 66 / 18 libs；test-vm 312；cargo 409 + threading 单测 + cross-thread smoke 全绿

#### Scenario: Compile-time Send+Sync 不可回归
- **WHEN** existing `send_sync.rs` assertions run
- **THEN** still pass（Module 加 Arc 不破坏 VmCore Send+Sync — Arc<T>: Send+Sync if T: Send+Sync；Module is Send+Sync 因 transitively 没有 !Send 字段）

### Requirement: GC + threading 安全保证（v0）

#### Scenario: 多线程下 alloc + collect 序列化但不死锁
- **GIVEN** 2 threads each repeatedly `new int[100]`
- **AND** one thread occasionally `Std.GC.Collect()`
- **WHEN** runs 10000 iterations
- **THEN** no panic / deadlock / segfault
- **AND** all alive objects survive

> **承认的限制（v0）**：mark-sweep 仍是 stop-the-world，从一个线程调用 `force_collect`。其他线程在分配路径上撞到 heap Mutex 时阻塞。性能差但正确。`add-gc-safepoint` + `add-concurrent-gc` 后改进。

## IR / VM Mapping

- 无新 opcode
- VmCore 新字段：`module: Arc<Module>`、`threads: Mutex<HashMap<u64, JoinHandle<...>>>`、`next_thread_id: AtomicU64`
- 新 VmContext 构造：`VmContext::new_with_core(Arc<VmCore>)`
- 新 builtins：`__thread_spawn` / `__thread_join`
- Stdlib `Std.Threading.Thread` 用 `[Native]` 标的 stub method 调上述 builtins

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] **VM corelib (native fn 实现)**
- [x] **VmContext / VmCore (字段扩展)**
- [x] interp 调用 path（spawned thread 入口）
- [ ] JIT —— 不动；JIT 模块 spawn 后跑 fallback interp（如需）
- [x] **Stdlib (z42.threading)**

## Anti-Scope

- 不引入 sync primitives（Mutex / Channel 用户类型）—— `add-sync-primitives`
- 不引入 spawn 语法 —— `add-spawn-syntax`（L3）
- 不引入 GC safepoint / concurrent collection —— `add-gc-safepoint` / `add-concurrent-gc`
- 不引入 thread_local 模拟 —— 用户用 GcRef 字段 + Mutex
- 不引入 Thread.Sleep / yield —— stdlib 可后续 add（在本 spec 实施期发现的)
- JIT 路径 spawn 不本期 ship
