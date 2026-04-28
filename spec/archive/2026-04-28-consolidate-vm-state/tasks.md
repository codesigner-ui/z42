# Tasks: consolidate-vm-state

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-28
> 类型：vm（完整流程：proposal + spec + design + tasks）

## 完成备注

- design.md 阶段实施期发现 ABI 限制，新增 **Decision 5** 把 JIT 端 2 个
  thread_local 保留为 extern-"C" 内部存储，靠 `JitModule::run` 边界
  `sync_in_from_ctx` / `sync_out_to_ctx` 与 VmContext 双向同步。外部观察
  等价：每个 ctx 看到的 static_fields / pending_exception 仍隔离。
- review2 §3 主目标兑现：lazy_loader STATE + interp STATIC_FIELDS + interp
  PENDING_EXCEPTION + UserException sentinel + user_throw / user_exception_take
  全部删除。
- runtime 内残留 thread_local：jit/helpers PENDING_EXCEPTION + STATIC_FIELDS（待
  follow-up `extend-jit-helper-abi` spec）+ jit/frame FRAME_POOL（pure cache，
  out of scope）。
- 验证：cargo test 68+4 ✅ / scripts/test-vm.sh 200/200 ✅ /
  scripts/test-cross-zpkg.sh 1/1 ✅ / dotnet test 734/734 ✅。

## 进度概览

- [x] 阶段 1：VmContext struct 落地
- [x] 阶段 2：interp 改造
- [x] 阶段 3：JIT 改造（含 ABI 扩展）
- [x] 阶段 4：lazy_loader 改造
- [x] 阶段 5：删除死代码 + 文档同步
- [x] 阶段 6：验证与归档

## 阶段 1：VmContext struct 落地

- [x] 1.1 新建 `src/runtime/src/vm_context.rs`：
  - `pub struct VmContext` 含 `static_fields: RefCell<HashMap<...>>`、
    `pending_exception: RefCell<Option<Value>>`、`lazy_loader: RefCell<Option<LazyLoader>>`
  - `impl VmContext`: `new()`, `static_get/set/clear`,
    `set_exception/take_exception`, `install_lazy_loader[_with_deps]`,
    `try_lookup_function/_type/_string`, `declared_namespaces`
- [x] 1.2 `src/runtime/src/lib.rs` 加 `pub mod vm_context;`
- [x] 1.3 写 `src/runtime/src/vm_context_tests.rs`：基础 unit test 验证
  static fields / pending_exception 行为；末尾 `#[cfg(test)] mod vm_context_tests;` 引入

## 阶段 2：interp 改造

- [x] 2.1 `interp/dispatch.rs`：
  - 删除 `STATIC_FIELDS` thread_local
  - `static_get/set/clear` 改成接受 `&VmContext` 的 free function
    （或直接调用方走 `ctx.static_get(...)`，删自由函数）
- [x] 2.2 `interp/exec_instr.rs`：
  - `exec_instr` 签名加 `ctx: &VmContext` 参数
  - `Instruction::StaticGet/StaticSet` 处理改用 `ctx.static_get/set`
- [x] 2.3 `interp/mod.rs`：
  - `exec_function(module, func, args)` → `exec_function(ctx, module, func, args)`
  - `run(module, func, args)` → `run(ctx, module, func, args)`
  - `run_with_static_init(module, func)` → `run_with_static_init(ctx, module, func)`
    - 内部 `dispatch::static_fields_clear()` → `ctx.static_fields_clear()`
- [x] 2.4 `interp/mod.rs`：删除 `PENDING_EXCEPTION` thread_local +
  `UserException` struct + `Display for UserException` + `user_throw` +
  `user_exception_take`
- [x] 2.5 `interp/mod.rs::exec_function` 内 `e.is::<UserException>()` 路径
  改为 `ctx.take_exception()` 后 wrap 为 `ExecOutcome::Thrown`

## 阶段 3：JIT 改造（含 ABI 扩展）

- [x] 3.1 `jit/frame.rs`：`JitModuleCtx` 加字段 `vm_ctx: *mut VmContext`
- [x] 3.2 `jit/helpers.rs`：
  - 删除 `STATIC_FIELDS` + `PENDING_EXCEPTION` 两个 thread_local
  - `set_exception` / `take_exception_error` / `static_get/set_inner/clear`
    改为 `unsafe fn` 接受 `*mut VmContext`（或 `&VmContext` —— 因为 RefCell）
- [x] 3.3 `jit/helpers_mem.rs`：所有 helper 内部通过 `(*ctx).vm_ctx` 拿
  `*mut VmContext` 后调 `set_exception` / `take_exception` 等
- [x] 3.4 `jit/helpers_object.rs`：
  - `jit_static_get` / `jit_static_set` 通过 `(*ctx).vm_ctx` 调
    `set/get` 方法
  - `jit_vcall` / `jit_call` 触发 set_exception 同样通过 vm_ctx
- [x] 3.5 `jit/helpers_arith.rs`：审查需要异常报告的 helper（除零等），
  改为通过 vm_ctx
- [x] 3.6 `jit/translate.rs`：helper 调用点不变（参数仍是 `*const JitModuleCtx`），
  只是 helper 内部访问字段方式变了；如果改 helper 签名为 `unsafe fn(*mut VmContext, ...)`
  则需更新 `declare_helpers` 的 sig
- [x] 3.7 `jit/mod.rs`：
  - `JitModule::run(&self, entry_name)` → `run(&mut self, ctx, entry_name)`
  - run 入口处把 `ctx as *mut VmContext` 写入 `self.ctx.vm_ctx`
  - run 出口处置空 `self.ctx.vm_ctx = std::ptr::null_mut()`
  - `compile_module` 不变（构造 ctx 时 vm_ctx = null）
  - `pub fn run(module, entry_name)` → `pub fn run(ctx, module, entry_name)`

## 阶段 4：lazy_loader 改造

- [x] 4.1 `metadata/lazy_loader.rs`：
  - 删除 `STATE` thread_local
  - 删除自由函数 `install` / `install_with_deps` / `uninstall` /
    `try_lookup_function` / `try_lookup_type` / `try_lookup_string` /
    `declared_namespaces`
  - 保留 `LazyLoader` struct 内部 + 关联 impl
  - VmContext 的对应方法委托给 `self.lazy_loader.borrow().as_ref()?.X`
- [x] 4.2 `metadata/loader.rs`：所有调 `lazy_loader::xxx` 处改 ctx 参数
- [x] 4.3 `metadata/lazy_loader_tests.rs`：重写为 ctx-based API

## 阶段 5：删除死代码 + 文档同步

- [x] 5.1 `vm.rs`：`Vm::run` → `run(&self, ctx, hint)`
- [x] 5.2 `main.rs`：构造 `VmContext`，传给 `vm.run(&mut ctx, ...)`
- [x] 5.3 `exception/mod.rs`：STATUS 描述更新（不再 PENDING_EXCEPTION
  thread_local；exception 状态在 VmContext.pending_exception；标记
  "Phase 2 模块仍 stub，待 Result<T, E> 等高级语法落地" 之类）
- [x] 5.4 `thread/mod.rs`：删去"3 处 thread_local"列表，反映现状
- [x] 5.5 `docs/design/vm-architecture.md`：增 "VmContext: 状态收口"段
  描述 VmContext 字段、生命周期、JIT 嵌入方式
- [x] 5.6 `docs/design/object-protocol.md`：异常传播说明微调（删
  UserException sentinel 历史段）

## 阶段 6：验证与归档

- [x] 6.1 `cargo build --manifest-path src/runtime/Cargo.toml` 通过
- [x] 6.2 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（>= 59 unit + 4 zbc_compat）
- [x] 6.3 `./scripts/test-vm.sh` 全绿（200/200 interp + jit）
- [x] 6.4 `./scripts/test-cross-zpkg.sh` 通过（lazy_loader 重构回归保护）
- [x] 6.5 `dotnet build src/compiler/z42.slnx` 通过（C# 端零改动，完备性）
- [x] 6.6 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 6.7 spec scenarios 逐条覆盖确认（spec.md 6 个 scenario）
- [x] 6.8 归档 `spec/changes/consolidate-vm-state/` →
  `spec/archive/2026-04-28-consolidate-vm-state/`
- [x] 6.9 自动 commit + push（含 `.claude/` 与 `spec/`）

## 备注

- 实施过程中若发现 helper 的 ctx 参数引发循环借用 / 编译错误，回阶段 6.5
  报告 + 调整 design.md 后继续
- review2 §3 + §5.5 在本 spec 完整覆盖
- 结束后 `runtime` 还剩 1 个 thread_local（`FRAME_POOL`，allocator cache，
  out of scope）
