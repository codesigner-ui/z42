# Proposal: consolidate-vm-state — VM 状态从 thread_local 收口到 VmContext

## Why

当前 runtime 共有 **6 个 thread_local 槽**承载 VM 状态：

| 槽 | 文件 | 用途 |
|---|------|------|
| `STATIC_FIELDS` (interp) | `interp/dispatch.rs` | 用户类 static 字段存储 |
| `PENDING_EXCEPTION` (interp) | `interp/mod.rs` | 几乎死代码（注释明说 interpreter 已改用 ExecOutcome::Thrown，仅 JIT bridge 用）|
| `STATIC_FIELDS` (JIT) | `jit/helpers.rs` | JIT 侧静态字段独立副本 |
| `PENDING_EXCEPTION` (JIT) | `jit/helpers.rs` | JIT extern "C" ABI 异常返回通道 |
| `FRAME_POOL` (JIT) | `jit/frame.rs` | 寄存器 Vec 复用池（pure cache，不在本变更范围）|
| `STATE` (lazy_loader) | `metadata/lazy_loader.rs` | 按需 zpkg 加载器单例 |

存在的问题（review2 §3 + §5.5）：

1. **多线程屏障**：z42 设计目标包含 "GC-safe concurrency"（README 第 7 行），但
   thread_local 强制 per-thread 隔离 → 用户代码无法在多线程间共享 static 字段
   或 lazy-loaded 类型；hot-reload + sandboxing 等同进程多 VM 实例场景被强制
   绑定到不同线程，丧失嵌入灵活性
2. **interp 与 JIT 静态字段不共享** —— `STATIC_FIELDS (interp)` 与
   `STATIC_FIELDS (JIT)` 是两份独立 HashMap，混合执行（如 interp 调用 JIT
   编译过的 helper）时静态字段读写不一致；当前测试不混合两种模式所以未暴露，
   是潜在 bug
3. **异常传播双系统**：interp 使用 `ExecOutcome::Thrown` enum 干净返回；JIT 走
   thread_local + `UserException` sentinel + `anyhow::Error` 包裹。两者通过
   `user_throw` / `user_exception_take` / `is::<UserException>()` 相互对接，
   控制流被塞进错误返回通道，理解成本高
4. **测试间状态污染**：`static_fields_clear()` 是手工调用的清场函数，任何
   忘了调的测试会被前一个测试污染；review2 §3 实证

## What Changes

引入显式 `VmContext` struct 持有运行时可变状态，所有 6 个 thread_local 槽中
有 5 个移到 ctx 内（FRAME_POOL 保留 —— 是 allocator cache，不是状态）。
异常传播路径统一到 `ExecOutcome::Thrown`，删除 `user_throw` /
`user_exception_take` / `UserException` sentinel + 配套 thread_local。

具体变化：

- 新增 `runtime/src/vm_context.rs`：`pub struct VmContext` 持有：
  - `static_fields: HashMap<String, Value>`
  - `pending_exception: Cell<Option<Value>>`（替代 JIT thread_local）
  - `lazy_loader: Option<LazyLoader>`（替代 lazy_loader::STATE thread_local）
- `interp::exec_function(...)` / `exec_instr(...)` / dispatch helpers 全部
  接受 `&mut VmContext`
- `jit::JitModule::run(...)` 接受 `&mut VmContext`；JIT helpers ABI 增加
  `*mut VmContext` 参数（最难一步，由 `JitModuleCtx` 嵌入）
- 删除 5 个 thread_local + interp 的 `PENDING_EXCEPTION` + `UserException`
  sentinel + `user_throw` + `user_exception_take`
- 异常路径：interp 全程 `ExecOutcome`；JIT 仍通过 ctx 字段（不是
  thread_local）；JIT-→-interp 桥仍通过 `ExecOutcome::Thrown` 转换
- `Vm::run` 签名调整：调用方先 `VmContext::new()` 再传给 `vm.run(&mut ctx, ...)`
- `lazy_loader::install/uninstall/try_lookup_*/declared_namespaces` 接受
  `&mut VmContext`（或 `&VmContext`，取决于读写）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | NEW | VmContext struct + 关联 impl |
| `src/runtime/src/lib.rs` | MODIFY | 暴露 vm_context 模块 |
| `src/runtime/src/vm.rs` | MODIFY | Vm::run 接受 &mut VmContext；删 ExecMode::Aot 内联 bail（迁到 aot.rs，aot.rs 已有但保持） |
| `src/runtime/src/main.rs` | MODIFY | 在 main 入口处构造 VmContext 并传递 |
| `src/runtime/src/interp/mod.rs` | MODIFY | 删 PENDING_EXCEPTION + UserException + user_throw + user_exception_take；`run` / `run_with_static_init` / `exec_function` 接受 &mut VmContext |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | exec_instr 接受 &mut VmContext；StaticGet/StaticSet 改用 ctx |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | 删 STATIC_FIELDS thread_local；static_get/set/clear 改 ctx 方法 |
| `src/runtime/src/jit/mod.rs` | MODIFY | JitModule::run 接受 &mut VmContext；compile_module 不变 |
| `src/runtime/src/jit/helpers.rs` | MODIFY | 删 STATIC_FIELDS + PENDING_EXCEPTION thread_local；改用 ctx；set_exception/take_exception_error 改签名 |
| `src/runtime/src/jit/helpers_mem.rs` | MODIFY | 接受 ctx；jit_set_ret / jit_throw / jit_install_catch 通过 ctx |
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | 接受 ctx；vcall / static_get / static_set 通过 ctx |
| `src/runtime/src/jit/helpers_arith.rs` | MODIFY | 接受 ctx（部分 helper 需要）|
| `src/runtime/src/jit/translate.rs` | MODIFY | declare_helpers + helper 调用插入 ctx 参数 |
| `src/runtime/src/jit/frame.rs` | MODIFY | FnEntry / JitFn type alias 加 ctx 参数 |
| `src/runtime/src/metadata/lazy_loader.rs` | MODIFY | 删 STATE thread_local；install/uninstall/try_lookup_* 接受 &mut VmContext / &VmContext |
| `src/runtime/src/metadata/loader.rs` | MODIFY | 调用 lazy_loader 处加 ctx 参数 |
| `src/runtime/src/exception/mod.rs` | MODIFY | 文档更新：STATUS 从 STUB 改为"已迁到 VmContext" |
| `src/runtime/src/thread/mod.rs` | MODIFY | 文档更新：删去"3 处 thread_local"列表，反映现状 |
| `docs/design/vm-architecture.md` | MODIFY | 增"VmContext" 段，描述状态收口设计 |
| `docs/design/object-protocol.md` | MODIFY | 异常传播说明微调（删 UserException sentinel 历史段）|

**只读引用**（用于理解上下文）：

- `src/runtime/src/interp/ops.rs` — int_binop 等 helper，确认是否需要 ctx
- `src/runtime/src/metadata/lazy_loader_tests.rs` — 看 lazy_loader 测试如何重写
- `src/runtime/src/metadata/loader.rs` — 引用 lazy_loader 入口

**变更类型枚举**：`NEW` / `MODIFY` 全部明确；无 DELETE / RENAME。

## Out of Scope

- **JIT FRAME_POOL** 保留为 thread_local —— 是 allocator cache 不是状态；
  多线程下每线程独立池子合理
- **多线程 z42 用户代码** —— 本变更解锁多 VM-per-process，但不引入 z42
  多线程语法（spawn / async 等仍是 L3+ 设计目标）
- **C# 编译器侧改动** —— 本变更纯 runtime；IR / zbc 格式 / 编译器流程零改动
- **测试间隔离改造** —— `lazy_loader_tests` 改写为 ctx-based 是顺手做；
  其他测试若需要 ctx 也补，但不主动重构其他测试

## Open Questions

- [ ] **JIT helpers ABI 改动方式**：选项 (a) 把 ctx 作为额外的 `*mut VmContext`
      参数加到所有 jit helper 签名；(b) 把 ctx 嵌入 `JitModuleCtx` 然后
      helpers 通过现有的 `*const JitModuleCtx` 参数访问。design.md 选 (b)
      —— 不改 ABI 仅扩字段；但 (a) 更清晰，待 user 确认
- [ ] **VmContext 是否 Clone**：默认 No（持有 lazy_loader 包含 zpkg paths 不
      宜复制）；若将来需要 hot-reload "fork ctx" 再加 explicit method
- [ ] **lazy_loader::install/uninstall 是否保留全局形式**：当前是 OnceCell
      风格的 install/uninstall pair；改为 ctx 方法后 install 变 `ctx.install_lazy_loader(...)`
      自然消失。design.md 按这个思路写
