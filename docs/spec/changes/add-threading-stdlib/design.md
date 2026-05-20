# Design: Std.Threading stdlib

## Architecture

```
当前（after add-vmcontext-registry）：
  VmCore {
    static_fields, ...,  heap, vm_contexts,
    // 没有 module；没有 threads registry
  }
  Vm { module: Module, default_mode: ExecMode }   ← Module 在 Vm，by value

本 spec 后：
  VmCore {
    static_fields, ...,  heap, vm_contexts,
    module: Arc<Module>,                     ← NEW: 跨线程共享
    threads: Mutex<HashMap<u64, JoinHandle<Result<(), anyhow::Error>>>>, ← NEW
    next_thread_id: AtomicU64,               ← NEW
  }
  Vm { default_mode: ExecMode, ... }         ← Module 移走

  // 新 entry：
  pub fn VmContext::new_with_core(core: Arc<VmCore>) -> Pin<Box<VmContext>>

  // 新 native fns：
  fn __thread_spawn(ctx, [action]) -> Value::I64 {
      let action_clone = action.clone();
      let core_clone = Arc::clone(&ctx.core);
      let id = core_clone.next_thread_id.fetch_add(1, Relaxed);
      let handle = std::thread::spawn(move || {
          let thread_ctx = VmContext::new_with_core(Arc::clone(&core_clone));
          run_action(&thread_ctx, &core_clone.module, action_clone)
              .map_err(|e| /* convert to user-visible */ e)
      });
      core_clone.threads.lock().insert(id, handle);
      Value::I64(id as i64)
  }
  
  fn __thread_join(ctx, [slot_id]) -> Value {
      let id = i64 from slot_id;
      let handle = ctx.core.threads.lock().remove(&id)
          .ok_or_else(|| throw ThreadException("thread already joined"))?;
      match handle.join() {
          Ok(Ok(()))  => Value::Null,
          Ok(Err(e))  => throw ThreadException with e.to_string(),
          Err(panic)  => throw ThreadException("thread panicked"),
      }
  }
```

## Decisions

### Decision 1: Module 位置 —— `Vm.module` vs `VmCore.module`

**问题**：Spawned thread 需要 Module 来跑 entry 函数。Module 在哪里？

**选项**：
- **A** `VmCore.module: Arc<Module>` —— 所有线程通过 VmCore 共享。Vm 不持有 Module
- **B** `Vm` 改为 `Arc<Vm>` —— spawned thread clone Arc 共享 Vm（含 Module）
- **C** spawn 闭包内 `Box::leak(Module)` —— hack，永不释放

**决定**：**A**。Module 是进程级全局状态（与 static_fields / heap 同性质）；属于 VmCore 自然。Vm 改为只持 `ExecMode` + run config，把 Module 引用从 VmCore 取。简化 Vm，与 VmCore 责任划分一致。

> 这是本 spec 最大的内部架构变更。所有现有 `Vm::new(module, mode)` callsite 都要改成构造 VmCore 时塞 module，然后 `Vm::new(mode)` 不再 take module。grep ~15 个 callsite。

### Decision 2: Action 类型签名

**问题**：`Thread.Start(action: Action) → Thread` 的 `action` 是什么 z42 类型？

**选项**：
- **A** `Action`（z42.core 中预定义的 0-arity void delegate）—— 公开 contract 清晰
- **B** generic `Func<TResult>`（含返回值）—— 但 Thread API 不需要返回值（Join 是 void）
- **C** untyped 'any callable' —— TypeChecker 路径复杂

**决定**：**A**。`public delegate void Action();` 在 z42.core/src/Delegates/Delegates.z42 已存在。`Thread.Start(Action action)` 签名干净。内部 native fn 接受 callable Value（可以是 Closure / FuncRef / Action 实例 —— 都走同一 dispatch path）。

### Decision 3: GC + threading 串行化策略

**问题**：v0 不引入 safepoint，多线程下 alloc/collect 怎么协调？

**选项**：
- **A** 接受 RcMagrGC 内部 Mutex 串行化 —— 性能差但正确，原子 happens-before 由 lock 保证
- **B** 实现 safepoint 协议 —— scope creep，单独 spec
- **C** 单线程 GC + reject 跨线程 alloc —— 太严格，无用

**决定**：**A**。设计上 v0 接受性能限制（"早期 threading；GC 串行；后续 spec 优化"）。在 design doc + concurrency.md "Runtime foundation 现状" 表明示记录。

实际行为：
- Thread A alloc → 上 heap Mutex → registry push → 释放
- Thread B collect_cycles → 上 heap Mutex → 扫 registry → 释放
- 两者并发但被 Mutex 序列化；无 race

### Decision 4: Thread handle 存放位置

**问题**：`std::thread::JoinHandle<...>` 不 Clone；如何让 z42 Thread 对象引用它？

**选项**：
- **A** `VmCore.threads: Mutex<HashMap<u64, JoinHandle>>` slot table（同 processes 模式）+ z42 `Thread._slot: long` field —— 跟 ProcessHandle 完全对称
- **B** 把 JoinHandle 直接装进 Value (内部 Arc<Mutex<Option<JoinHandle>>>) —— 但 Value 不知道这个类型，要新 variant
- **C** 用 GCHandle slab —— 借用现有 handle table

**决定**：**A**。与 `Std.IO.Process` 模式一致（add-std-process 2026-05-14）；最小化新基建；slot id 透过 `Thread._slot: long` 字段持有，Take-on-join 释放干净。

### Decision 5: Action throw 跨线程语义

**问题**：spawned action `throw new Exception("boom")` 后 main thread `Join()` 看到什么？

**选项**：
- **A** Join 抛 `Std.ThreadException`，inner message 含原异常 message
- **B** Join 抛原异常类型 —— 但跨线程异常对象 Send 难（需序列化 / 复制）
- **C** 静默吞掉，set Thread._failed = true，调用方查 —— 太宽松

**决定**：**A**。简单可靠。`ThreadException : Exception`（namespace Std，跟 z42.io 的 ProcessStartException 同模式）。Inner message format："thread N action threw: <msg>"。原异常 stack trace 暂不跨线程透传（v0 限制；后续可加）。

### Decision 6: panic 跨线程语义

**问题**：spawned action 触发 Rust panic（非 z42 throw）—— 比如 Mutex poison、index out of bounds 等。

**决定**：spawn 闭包内 `std::panic::catch_unwind`，捕获后转 `anyhow::Error::msg("panic in spawned thread")`，从 `__thread_join` 抛 ThreadException。比静默吞强；与 z42 throw 路径同一返回机制。

### Decision 7: Join idempotence

**问题**：`var t = Thread.Start(...); t.Join(); t.Join();` 第二次 Join 怎么办？

**选项**：
- **A** 抛 `ThreadException("thread already joined")` —— C# 风格
- **B** silent no-op —— Python 风格

**决定**：**A**。slot id take-on-join 后 HashMap.remove 返 None；slot 无 → throw。错误立即可见。

### Decision 8: 默认 thread count limit

**问题**：用户狂 spawn N 千个 thread 怎么办？

**决定**：**不加 limit**（v0）。OS 自带 thread 上限；超 OS 会 spawn 返 Err，我们 throw。后续 spec（threadpool）按需加。

## Implementation Notes

### 实施顺序

1. **Phase 1** ── VmCore module field：`module: Arc<Module>` 加进 VmCore；构造路径调整；Vm 瘦身去 module
2. **Phase 2** ── threads registry：`VmCore.threads` + `next_thread_id`
3. **Phase 3** ── `VmContext::new_with_core` 构造函数
4. **Phase 4** ── 两个 native fns（`__thread_spawn` / `__thread_join`）+ 注册
5. **Phase 5** ── stdlib z42.threading 包（Thread.z42 / ThreadException.z42）+ workspace.toml + build-stdlib.sh
6. **Phase 6** ── 4 个 stdlib tests
7. **Phase 7** ── docs：concurrency.md / overview.md / organization.md / roadmap.md
8. **Phase 8** ── 归档 + commit + push

### Vm 改造影响面

```rust
// before:
pub struct Vm {
    pub module: Module,
    pub default_mode: ExecMode,
}
impl Vm {
    pub fn new(module: Module, mode: ExecMode) -> Self { ... }
    pub fn run(&self, ctx: &VmContext, hint: Option<&str>) -> Result<()> { ... }
}

// after:
pub struct Vm {
    pub default_mode: ExecMode,
}
impl Vm {
    pub fn new(mode: ExecMode) -> Self { ... }
    pub fn run(&self, ctx: &VmContext, hint: Option<&str>) -> Result<()> {
        // 从 ctx.core.module 取 module
        ...
    }
}
```

Caller pattern 变化：
```rust
// before:
let ctx = VmContext::new();
let vm = Vm::new(module, ExecMode::Interp);
vm.run(&ctx, hint)?;

// after:
let core = VmCore::new(module);   // 新构造函数
let ctx = VmContext::new_with_core(Arc::new(core));
let vm = Vm::new(ExecMode::Interp);
vm.run(&ctx, hint)?;

// or, keeping the old VmContext::new() default-VmCore path:
// VmContext::new() internally constructs VmCore with a default Module
// — but we don't have a "default" Module concept. So `new()` becomes:
//   - Accept the module to construct VmCore inside
//   - OR removed entirely; force callers to construct VmCore explicitly
```

**决定**：`VmContext::new(module: Module) -> Pin<Box<VmContext>>` 接受 module 直接构造 VmCore；`VmContext::new_with_core(Arc<VmCore>)` 是分支 entry 给 spawned thread。两个 entry：

```rust
impl VmContext {
    /// Standard entry: caller provides the user's compiled Module.
    pub fn new(module: Module) -> Pin<Box<Self>> {
        let core = Arc::new(VmCore { module: Arc::new(module), ..default });
        Self::new_with_core(core)
    }
    /// Spawned-thread entry: share an existing VmCore.
    pub fn new_with_core(core: Arc<VmCore>) -> Pin<Box<Self>> {
        // construct VmContext referencing existing core; register self
        ...
    }
}
```

这要求所有现有 `VmContext::new()` 0-arg 调用点改为 `VmContext::new(module)`。grep ~15 callsite。

> **API 变更范围比 add-vmcontext-registry 大**：本 spec 实施期需要：
> 1. `Vm::new` 签名变（module 参数走 VmContext 而非 Vm）
> 2. `VmContext::new` 签名变（加 module 参数）
> 3. caller pattern 全部更新

> 实施期 task 排前面验收，避免后续 cascading issues。

### __thread_spawn 内部 dispatch

```rust
pub(crate) fn builtin_thread_spawn(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let action = args.get(0).ok_or_else(|| anyhow!("__thread_spawn: missing action"))?;
    // Validate callable
    let (fn_name, env) = match action {
        Value::Closure { fn_name, env } => (fn_name.clone(), Some(env.clone())),
        Value::FuncRef(name) => (name.clone(), None),
        // Action delegate is a Closure under the hood for most cases
        other => bail!("__thread_spawn: expected Action / Closure / FuncRef, got {}", other.kind()),
    };
    
    let core = Arc::clone(&ctx.core);
    let id = core.next_thread_id.fetch_add(1, Ordering::Relaxed);
    
    let handle = std::thread::spawn(move || -> Result<(), anyhow::Error> {
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let thread_ctx = VmContext::new_with_core(core);
            let module = &thread_ctx.core.module;
            let entry = module.functions.iter().find(|f| f.name == fn_name)
                .ok_or_else(|| anyhow!("spawned action fn `{}` not found", fn_name))?;
            // Spawn-time args: empty for Action; env injected as frame's first slot if Closure
            let args = match env {
                Some(env_gc) => vec![Value::Closure { fn_name: fn_name.clone(), env: env_gc }],
                None => vec![],
            };
            crate::interp::run_with_static_init(&thread_ctx, module, entry)
                .map_err(|e| anyhow!("spawned action: {}", e))
        }));
        match result {
            Ok(r) => r,
            Err(_) => Err(anyhow!("thread panicked (Rust panic; not user throw)")),
        }
    });
    
    core.threads.lock().insert(id, handle);
    Ok(Value::I64(id as i64))
}
```

> 实施期可能发现 closure env 注入路径不简单（Action 不是 0-arg？fn_name 怎么 resolve callback fn body？interp::run_with_static_init 接受啥参数？）。这是 verify-on-implementation 项。

### 测试 fixture 形态

```z42
// tests/thread_basic.z42
namespace Z42ThreadingBasicTests;
using Std;
using Std.Test;
using Std.Threading;

[Test]
void test_spawn_then_join_empty_action() {
    var t = Thread.Start(() => { });
    t.Join();
    Assert.True(true);  // No exception thrown is the success criterion
}
```

## Testing Strategy

- **单元测试**：corelib/threading_tests.rs（Rust 直接构造 VmContext + 调 builtin，不经过 z42 脚本）
- **集成测试**：cross_thread_smoke.rs 加 `spawn_via_builtin_then_join`
- **stdlib z42 测试**：4 个 z42 文件（basic / shared_static / throws / many）
- **GREEN gate**：stdlib 62 + 4 = 66；test-vm 312；cargo 单测全过
- **手动 stress**：跑 thread_many 100 次确认无 race-condition flake
