# Tasks: Inline JIT safepoint fast-path

> 状态：🔴 BLOCKED — bench `04_c2_p1_arith_loop` panics on Linux x86_64 in
> first warmup run (exit code 101); reverted 2026-06-05 to unblock CI / 0.2.0
> release. macOS aarch64 runs the inline path fine. Pending Linux repro +
> root cause investigation before re-attempting.
> 创建：2026-06-03 | revert：2026-06-05 | 类型：refactor + perf

## 阻塞详情

- **触发**：bench-update.yml workflow on Ubuntu — `hyperfine` reports
  "Command terminated with non-zero exit code 101 in the first warmup run"
  for `04_c2_p1_arith_loop` only (other 3 scenarios pass).
- **复现失败**：`Z42_LIBS=... z42vm --mode jit /tmp/c2_p1.zbc Bench.C2P1ArithLoop.Main`
  works on macOS aarch64 (verified 2026-06-05).
- **推测**：x86_64 Cranelift lowering of `atomic_rmw.i32 Sub` against
  `VmContext.safepoint_skip` (non-`#[repr(C)]` field, offset via
  `offset_of!`) — alignment / addressing mode something. Needs Linux dev
  loop to triage.
- **临时处理**：`git revert 31cee6c1` (2026-06-05) restores helper-call
  emission. Spec moved back to `changes/` with BLOCKED status. Slow-path
  helper `check_safepoint_slow_with_reset` left in place (dead code, harmless,
  ready for re-use).

---

# Tasks: Inline JIT safepoint fast-path

> 状态：🟡 进行中 | 创建：2026-06-03 | 类型：refactor + perf
> 来源：[`docs/review.md`](../../../review.md) C2 P1 deferred `jit-future-safepoint-inline`（见 archive [2026-05-28-jit-type-specialization tasks.md](../../archive/2026-05-28-jit-type-specialization/tasks.md#out-of-scope-items-deferred-for-future-spec)）

## 变更说明

每个 backward `Br` / `BrCond` / 函数 entry / `Call` 返回点，JIT 现在 emit `call hr_check_safepoint(frame, ctx)`。helper 调用 ~10ns 开销（caller-save spill + jump + return），但 fast path 本体只是：

```rust
let prev = ctx.safepoint_skip.fetch_sub(1, Ordering::Relaxed);
if prev > 1 { return; }
```

约 3-5ns。helper 调用开销大于实际工作。inline 成 Cranelift `atomic_rmw sub + brif`，仅 slow path 走 helper。

预期：SumSquares 10M-iter loop 从 **1.51× → ~1.8×**（C2 P1 spec 中 deferred 时 call out 的剩余 gap）。

## 原因

`hr_check_safepoint` 在 hot loop 上每个后向跳转都 fire 一次。Cranelift `atomic_rmw sub` 在 x86-64 是单条 `LOCK XADD`、aarch64 是 `ldsetal/ldadd` —— 远比 C ABI helper call 便宜。slow path 仍走 helper（counter == 0 / 1 时 0.1% 的实际 GC drain）。

## 文档影响

- `docs/review.md` Part 5 P4 状态 (🟡 done) + Part 2 C2 P1 "remaining gap" 备注更新
- `docs/design/runtime/vm-architecture.md` JIT codegen 章节加 safepoint inline 节（实现原理 + atomic_rmw 选型理由）
- archived spec [2026-05-28-jit-type-specialization tasks.md](../../archive/2026-05-28-jit-type-specialization/tasks.md#out-of-scope-items-deferred-for-future-spec) Deferred 段标记已 land + 指向本 spec archive 路径

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/gc/safepoint.rs` | MODIFY | `check_safepoint_slow` 改 `pub(crate)`；加新 fn `check_safepoint_slow_with_reset(ctx)` 做"counter reset + slow check"两步原子动作 |
| `src/runtime/src/jit/helpers/control.rs` | MODIFY | 加 `jit_check_safepoint_slow` 函数（counter reset + 调 slow）；保留 `jit_check_safepoint` 作 fallback / non-JIT 路径 |
| `src/runtime/src/jit/helpers/registry.rs` | MODIFY | 注册 `jit_check_safepoint_slow` helper id |
| `src/runtime/src/jit/frame.rs` | MODIFY | 加 `JIT_MODULE_CTX_VM_CTX_OFFSET` const（`offset_of!(JitModuleCtx, vm_ctx)`） |
| `src/runtime/src/vm_context.rs` | MODIFY | 加 `VM_CONTEXT_SAFEPOINT_SKIP_OFFSET` const（`offset_of!(VmContext, safepoint_skip)`） |
| `src/runtime/src/jit/translate.rs` | MODIFY | 加 `emit_safepoint_check(builder, ctx_val, frame_val, hr_slow, helper_ids)`；替换 5 处 `hr_check_safepoint` call 为该 emit |
| `src/runtime/src/jit/helpers/control_tests.rs` (if exists) / inline tests | MODIFY | 加 unit test：JIT-compiled function 在 GC pause 下正确 park |

## 设计要点

### atomic_rmw + brif 的 emit 模式

5 处 safepoint 插入点（function entry + backward Br + BrCond branches + after Call return）每处 emit 5 条 Cranelift IR：

```
v_vm_ctx     = load.i64 trusted, ctx_val + JIT_MODULE_CTX_VM_CTX_OFFSET
v_skip_addr  = iadd_imm v_vm_ctx, VM_CONTEXT_SAFEPOINT_SKIP_OFFSET
v_one        = iconst.i32 1
v_prev       = atomic_rmw.i32 trusted, Sub, v_skip_addr, v_one
v_cmp        = icmp_imm ugt v_prev, 1
brif v_cmp, fast_block, slow_block

fast_block:    // continue with rest of caller's logic
  ...

slow_block:
  call hr_check_safepoint_slow(frame_val, ctx_val)
  jump fast_block
```

每个 emit site 创建两个新 cranelift block（fast / slow）；slow block 调 helper 后无条件跳回 fast block。fast block 成为后续 emit 的当前 block。

### 为什么不用一个 shared slow_block

每个 emit site 的"后续逻辑"不同 —— shared slow_block 跳回单一 fast block 后还要分支决定下一步，反而复杂。每 site 两个本地 block + 直接 jump 是最简单的拓扑。Cranelift block 是 cheap（几十 bytes），5 个 site × 2 block = 10 个 block 开销可忽略。

### Cranelift atomic_rmw 的 ordering

Cranelift 不支持 ordering 参数 —— `atomic_rmw` 默认是 SeqCst。x86-64 上 `LOCK XADD` 已是 SeqCst（无 cost difference）；aarch64 `ldsetal` 是 acquire-release（略强于 Rust 的 Relaxed）。我们这里只需要 Relaxed 语义（counter 不 carry 其他数据的 ordering 依赖），但 Cranelift 强制 SeqCst 不影响正确性，只是 aarch64 上 ~1ns 略慢。仍远快于 helper call 的 10ns。

### offset_of 安全性

`std::mem::offset_of!` 是 stable Rust 1.77+（我们的 MSRV 早过此版本）。返回 `usize` const，在编译期 evaluate，与字段顺序无关 —— Rust 编译器可能重排字段，但 `offset_of!` 总是给出 actual offset。

`#[serde(skip)]` 之类的 attribute 不影响 layout。`#[repr(Rust)]` 默认下编译器选最紧凑布局，但同一 build 内稳定。

### 为什么保留 `jit_check_safepoint`

仍保留：
1. 测试代码直接调用（`jit_check_safepoint` 已有 inline tests 覆盖 idle + drain auto-collect 两个场景）
2. 任何未来非 hot path 的 emit site 可继续用 helper（清晰度优先于 1ns 节省）
3. 是 `jit_check_safepoint_slow` + 前置 `fetch_sub` 的语义合体，作为 reference implementation 文档化

inline 路径仅替换 hot path 的 5 处 emit 调用。

## 任务

- [ ] 0.1 NEW spec `tasks.md`（本文件）
- [ ] 1.1 MODIFY `gc/safepoint.rs` 加 `pub(crate) fn check_safepoint_slow_with_reset(ctx)`
- [ ] 1.2 MODIFY `jit/helpers/control.rs` 加 `jit_check_safepoint_slow`（counter reset + slow check）
- [ ] 1.3 MODIFY `jit/helpers/registry.rs` 注册 `check_safepoint_slow` FuncId
- [ ] 1.4 MODIFY `vm_context.rs` 加 `pub const VM_CONTEXT_SAFEPOINT_SKIP_OFFSET`
- [ ] 1.5 MODIFY `jit/frame.rs` 加 `pub const JIT_MODULE_CTX_VM_CTX_OFFSET`
- [ ] 1.6 MODIFY `jit/translate.rs` 加 `emit_safepoint_check(builder, ctx_val, frame_val, helper_ids)` 工具函数
- [ ] 1.7 MODIFY `jit/translate.rs` 替换 5 处 `hr_check_safepoint` call 为 `emit_safepoint_check`（function entry + 4 处 Br/BrCond/Call）
- [ ] 2.1 VERIFY `cargo build --release` clean + `cargo test --lib` 全过
- [ ] 2.2 VERIFY `./scripts/test-all.sh` 全绿
- [ ] 2.3 (optional) bench `04_c2_p1_arith_loop.z42` 验证 1.51× → ~1.8× delta
- [ ] 3.1 MODIFY `docs/review.md` 标 P4 hot-path stub inline → done + 更新 Part 2 C2 P1 "remaining gap" 备注
- [ ] 3.2 MODIFY `docs/design/runtime/vm-architecture.md` JIT codegen 章节加 safepoint inline 节
- [ ] 3.3 MODIFY archived 2026-05-28-jit-type-specialization tasks.md Deferred 段标记 jit-future-safepoint-inline 已 land
- [ ] 4.1 归档 + commit + push
