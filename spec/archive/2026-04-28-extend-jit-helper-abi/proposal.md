# Proposal: extend-jit-helper-abi — 删除 JIT 端最后 2 个 thread_local

## Why

`consolidate-vm-state`（2026-04-28）把 6 个 thread_local 中的 4 个迁到了
`VmContext`，但 JIT 端 `helpers.rs` 留下 2 个：

- `PENDING_EXCEPTION: thread_local! RefCell<Option<Value>>`
- `STATIC_FIELDS:     thread_local! RefCell<HashMap<String, Value>>`

保留它们的原因（design.md Decision 5）：30+ 个 extern "C" arith/bool helper
签名是 `fn(frame, dst, a, b) -> u8`，不带 `*const JitModuleCtx`，无法直接拿
到 `VmContext`。当时通过 `JitModule::run` 边界 `sync_in_from_ctx` /
`sync_out_to_ctx` 双向同步绕过。

代价：

1. **同步开销**：每次 JIT 调用边界 clone 整个 static_fields HashMap（小但浪费）
2. **同线程并发不可能**：sync 模式假定 `JitModule::run` 串行执行；多 ctx 真
   并发跑 JIT 时 thread_local 仍会串
3. **运行时残留 2 处 thread_local 状态**，与 review2 §3 完整目标差最后一步

本变更把所有 31 个 extern "C" helper 加 `ctx: *const JitModuleCtx` 第 2 参
（剩 6 个 helper 已经有 ctx），删除 2 个 thread_local + sync 桥接，达到 review2 §3 完整目标。

## What Changes

- `jit/helpers.rs`：
  - 删除 `PENDING_EXCEPTION` + `STATIC_FIELDS` thread_local
  - 删除 `sync_in_from_ctx` / `sync_out_to_ctx` / `static_fields_clear`
  - `set_exception` / `take_exception` / `take_exception_error` /
    `static_get` / `static_set_inner` 改签名接 `&VmContext`
  - 新增 `unsafe fn vm_ctx_ref<'a>(ctx: *const JitModuleCtx) -> &'a VmContext`
    工具，统一 helper 内部 ctx 解引用 + safety contract 收口
- `jit/helpers_arith.rs`：8 个 helper 签名加 `ctx: *const JitModuleCtx` 第
  2 参；`set_exception(...)` 内部走 `vm_ctx_ref(ctx).set_exception(...)`
- `jit/helpers_mem.rs`：13 个 helper（1 已带 ctx），同上
- `jit/helpers_object.rs`：15 个 helper（5 已带 ctx），同上；`jit_static_get`
  / `jit_static_set` 通过 ctx 访问字段
- `jit/translate.rs`：
  - `declare_helpers` 内 ~30 个 `decl!` 在第 1 个 ptr 后插入 ctx ptr
  - helper 调用点 ~30 处插入 `ctx_val` 第 2 参
- `jit/mod.rs`：`JitModule::run` 删除 `sync_in_from_ctx` / `sync_out_to_ctx`
  调用；`vm_ctx` 字段写入仍保留（helper 通过它拿 ctx）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/jit/helpers.rs` | MODIFY | 删 thread_local + sync helpers；新 vm_ctx_ref 工具；set_exception 等改签名 |
| `src/runtime/src/jit/helpers_arith.rs` | MODIFY | 8 helper 签名 + 内部访问改写 |
| `src/runtime/src/jit/helpers_mem.rs` | MODIFY | 13 helper 签名 + 内部访问改写 |
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | 15 helper 签名 + 内部访问改写 |
| `src/runtime/src/jit/translate.rs` | MODIFY | declare_helpers ptr 插入 + 调用点 ctx_val 插入 |
| `src/runtime/src/jit/mod.rs` | MODIFY | 删 sync 调用 |
| `src/runtime/src/exception/mod.rs` | MODIFY | 文档更新：runtime 已无 thread_local 异常状态 |
| `src/runtime/src/thread/mod.rs` | MODIFY | 同上 |
| `docs/design/vm-architecture.md` | MODIFY | 文档：JIT helper ABI 已扩展，sync 段落删除 |
| `spec/archive/2026-04-28-consolidate-vm-state/design.md` | 只读 | 历史 Decision 5 仍存档，不动 |

## Out of Scope

- `jit/frame.rs::FRAME_POOL` thread_local（pure allocator cache，保留）
- 多线程 JIT 并发执行（虽然 ABI 改造解锁，但 `JitModule::run` 仍是同步的，
  真正并发支持需要进一步重构 frame_pool / jit_module 共享）
- C# 编译器侧零改动（IR / zbc 格式不变）

## Open Questions

无。设计已在 `consolidate-vm-state` Decision 5 完整描述（"完整 ABI 改造留作
follow-up spec `extend-jit-helper-abi`"）。
