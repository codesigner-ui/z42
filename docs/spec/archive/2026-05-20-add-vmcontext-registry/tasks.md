# Tasks: add VmContext registry for cross-thread GC root scanning

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm

## 进度概览
- [x] 阶段 1: 类型扩展（VmContextPtr / vm_contexts 字段 / _pin 字段）
- [x] 阶段 2: 构造路径 `Pin<Box<VmContext>>` + registration / Drop
- [x] 阶段 3: GC scanner closure 走 registry
- [x] 阶段 4: caller API 适配
- [x] 阶段 5: 测试 + 文档
- [x] 阶段 6: 归档 + 提交

## 阶段 1: 类型扩展

- [x] 1.1 `src/runtime/src/vm_context.rs` 加 `pub(crate) struct VmContextPtr(pub(crate) *const VmContext);` + SAFETY-doc + `unsafe impl Send / Sync`
- [x] 1.2 同文件 VmCore 加 `pub(crate) vm_contexts: parking_lot::Mutex<Vec<VmContextPtr>>` 字段 + 初始化 `Mutex::new(Vec::new())`
- [x] 1.3 VmContext 加 `_pin: std::marker::PhantomPinned` 字段 + 初始化 `PhantomPinned`
- [x] 1.4 `cargo build --release` GREEN（此时 new() 仍返回 VmContext，未 boxed；先验证类型层无 cascade error）

## 阶段 2: 构造 + Drop

- [x] 2.1 `VmContext::new()` 签名改 `pub fn new() -> Pin<Box<VmContext>>`
- [x] 2.2 实现：先 `Box::new(VmContext { ..., _pin: PhantomPinned })`，然后 `unsafe { Pin::new_unchecked(boxed) }`，最后 `ctx.core.vm_contexts.lock().push(VmContextPtr(&*ctx))`
- [x] 2.3 `impl Default for VmContext` 移除（无法保持 by-value default 语义；如有 callsite 改用 `VmContext::new()`）
- [x] 2.4 `impl Drop for VmContext`：`let ptr = self as *const _; self.core.vm_contexts.lock().retain(|p| p.0 != ptr);`
- [x] 2.5 单测 `vm_context_tests.rs` 加 `vm_context_registers_self_on_new`、`vm_context_drop_removes_from_registry`

## 阶段 3: GC scanner closure

- [x] 3.1 `VmContext::new()` 内 scanner 安装代码：移除 `pe / cs / frs` 三个 Arc clone 捕获
- [x] 3.2 Scanner closure 新实现：
  ```rust
  if let Some(c) = core_weak.upgrade() {
      for v in c.static_fields.lock().iter() { visit(v); }
      let registry = c.vm_contexts.lock();
      for ctx_ptr in registry.iter() {
          unsafe {
              let ctx = &*ctx_ptr.0;
              if let Some(v) = ctx.pending_exception.lock().as_ref() { visit(v); }
              for frame in ctx.call_stack.lock().iter() {
                  for v in (*frame.regs).iter() { visit(v); }
                  if !frame.env_arena.is_null() {
                      for env in (*frame.env_arena).iter() {
                          for v in env.iter() { visit(v); }
                      }
                  }
              }
              for v in ctx.func_ref_slots.lock().iter() { visit(v); }
          }
      }
  }
  ```
- [x] 3.3 verify 没有 alloc 路径在 vm_contexts.lock() 内（Decision 3 deadlock 顾虑）

## 阶段 4: caller API 适配

- [x] 4.1 `src/runtime/src/vm.rs::Vm::run`：参数 `&mut VmContext` → `&VmContext`
- [x] 4.2 `src/runtime/src/jit/mod.rs`：3 个 `&mut VmContext` callsite 同上
- [x] 4.3 `src/runtime/src/main.rs`：`let mut ctx = VmContext::new();` → `let ctx = VmContext::new();`
- [x] 4.4 `src/runtime/src/host/state.rs`：`HostModule.ctx: VmContext` → `Pin<Box<VmContext>>`
- [x] 4.5 `src/runtime/src/host/ops.rs`：构造 / take 路径适配
- [x] 4.6 `src/runtime/src/host/mod.rs`：API 入口适配
- [x] 4.7 `src/toolchain/test-runner/src/bootstrap.rs`：`let ctx = VmContext::new();` 调整 + `LoadedRunner.ctx` 类型
- [x] 4.8 cargo build (dev + release) GREEN

## 阶段 5: 测试 + 文档

- [x] 5.1 `src/runtime/src/gc/arc_heap_tests/send_sync.rs` 加 `assert_send_sync::<VmContextPtr>()`
- [x] 5.2 `src/runtime/tests/cross_thread_smoke.rs` 加 `multi_vm_contexts_alloc_and_collect` 测试（scope 详 design.md Decision 7；可能简化为同 VmCore 多 VmContext 的注册表行为验证而非真跑两个线程脚本）
- [x] 5.3 `./scripts/test-stdlib.sh` 不回归
- [x] 5.4 `./scripts/test-vm.sh` 不回归
- [x] 5.5 `cargo test --release` 全绿（414 + 新增）
- [x] 5.6 `docs/design/runtime/vm-architecture.md` "VmContext / VmCore" 章节更新：删除 "single-VmContext-per-VmCore 不变量" 描述；加 registry 与 scanner 重写说明
- [x] 5.7 `docs/design/runtime/concurrency.md` "Runtime foundation 现状" 表："多 VmContext 共享 GC heap" 行从 ❌ 改 ✅；移除 single-invariant 表行
- [x] 5.8 `docs/design/runtime/gc-handle.md` 如有 scanner 描述同步

## 阶段 6: 归档 + 提交

- [x] 6.1 mv → `docs/spec/archive/2026-05-20-add-vmcontext-registry/`（同日落地，跟 add-multithreading-foundation 一组）
- [x] 6.2 commit + push（建议分 commit：阶段 1+2 一个，阶段 3+4 一个，阶段 5+6 一个；不囤积）
- [x] 6.3 verify CI 通过

## 备注

（实施中发现写这里）

—

## 后续相关 spec（依赖顺序）

| 名称 | 范围 | 依赖 |
|------|------|------|
| `add-threading-stdlib` | `Std.Threading.Thread.Start` / `.Join` / per-thread heap-local | 本 spec |
| `add-sync-primitives` | `Std.Threading.Mutex<T>` / `Channel<T>` 用户类型 | `add-threading-stdlib` |
| `add-gc-safepoint` | interp + JIT safepoint，并发 GC 前置 | 本 spec |
| `add-concurrent-gc` | mark-sweep 并发化（Phase A 性能轨道） | `add-gc-safepoint` |
| `add-spawn-syntax` | `spawn` / `task scope` 语言层（L3，concurrency.md §3.5） | `add-threading-stdlib` |
