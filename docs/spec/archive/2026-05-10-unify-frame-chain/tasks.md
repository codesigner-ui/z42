# Tasks: Unify Frame Chain (interp / jit / gc / stack-trace)

> 状态：🟢 已完成 | 创建：2026-05-10 | 完成：2026-05-10
> 类型：refactor + bugfix（最小化模式）

**变更说明：** 把 VmContext 当前 3 条平行栈（`exec_stack` GC roots / `env_arena_stack` GC roots / `call_stack` 调用 trace）合并成单一 `Vec<VmFrame>`，每个 VmFrame 同时承载 (regs ptr, env_arena ptr, func_name, file, line, column)。一次 push 一次 pop 等价旧的 6 次操作；调用者无法再"忘记 push 其中一份"。

**附带修复 2 个隐性 bug：** [jit/helpers/object.rs:69](../../src/runtime/src/jit/helpers/object.rs#L69) `jit_obj_new` 调 ctor + [jit/helpers/value.rs:123](../../src/runtime/src/jit/helpers/value.rs#L123) `jit_to_str` 调 ToString 都只 push frame_state（GC）没 push call_frame，导致 stack trace 缺这些 JIT 帧。统一接口后所有 invoke 站点强制 push 全套。

**原因：** 接续上批 jit-stack-trace + exception-stack-trace。三栈分立是 "演化遗留"（每加一个用途就加一条 Vec），新需求（debugger / tier-up frame snapshot）会再加第 4 条。统一是 M7 期间 review.md Part 4 §4.5 列项，也是后续 `eh-protocol-v2` / `lazy-metadata-loading` 的前置。

## 阶段 1: 定义 VmFrame + 替换 VmContext 字段

- [x] 1.1 [src/runtime/src/exception/mod.rs](../../src/runtime/src/exception/mod.rs) — `FrameInfo` 升级为 `VmFrame`，加 `regs: *const Vec<Value>` / `env_arena: *const Vec<Vec<Value>>` 字段
  - 保留 `func_name / file / line / column` 不变
  - `Cell<u32>` 仍用于 line/column 可变
  - 加 SAFETY doc：raw ptr 由 RAII 保证 frame 活
- [x] 1.2 [src/runtime/src/vm_context.rs](../../src/runtime/src/vm_context.rs) — 删 `exec_stack` / `env_arena_stack`；`call_stack: Vec<VmFrame>` 承担三责。GC root scanner 闭包改为单循环。
- [x] 1.3 新 API：`push_frame(VmFrame)` / `pop_frame()` 合并 push_frame_state + push_call_frame；`update_top_frame_pos(line, col)` 不变；`frame_state_at(idx) / frame_stack_depth()` 改读 call_stack
- [x] 1.4 删除旧 API：`push_frame_state` / `pop_frame_regs` / `push_call_frame` / `pop_call_frame`

## 阶段 2: 迁移 push/pop 站点

每站点旧两行 push（state + call_frame）→ 新一行 push_frame(VmFrame::new(...))。

- [x] 2.1 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `exec_function` 入口
- [x] 2.2 [src/runtime/src/jit/mod.rs](../../src/runtime/src/jit/mod.rs) `JitModule::run_fn`
- [x] 2.3 [src/runtime/src/jit/helpers/call.rs](../../src/runtime/src/jit/helpers/call.rs) `jit_call`
- [x] 2.4 [src/runtime/src/jit/helpers/closure.rs](../../src/runtime/src/jit/helpers/closure.rs) `jit_call_indirect`
- [x] 2.5 [src/runtime/src/jit/helpers/vcall.rs](../../src/runtime/src/jit/helpers/vcall.rs) `jit_vcall`（4 个 invoke 路径）
- [x] 2.6 [src/runtime/src/jit/helpers/object.rs](../../src/runtime/src/jit/helpers/object.rs) `jit_obj_new` ctor 调用 — **bugfix：之前只 push frame_state，现在 push 完整 VmFrame 含 callee name**
- [x] 2.7 [src/runtime/src/jit/helpers/value.rs](../../src/runtime/src/jit/helpers/value.rs) `jit_to_str` ToString 调用 — **bugfix 同上**

## 阶段 3: FrameGuard 简化

- [x] 3.1 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `FrameGuard` Drop 现在只调 `pop_frame()`（单一 API）

## 阶段 4: GC root scanner 单循环

- [x] 4.1 [src/runtime/src/vm_context.rs](../../src/runtime/src/vm_context.rs) `set_external_root_scanner` 闭包改为 `for frame in cs.borrow().iter() { unsafe { … (*frame.regs).iter()  / (*frame.env_arena).iter() … } }`

## 阶段 5: ref/closure/Stack 路径验证

`Value::Ref { kind: RefKind::Stack { frame_idx } }` 通过 `frame_state_at(idx)` 拿 regs 指针。新 API 返回 `frame.regs`（同一指针），保持兼容。

- [x] 5.1 [src/runtime/src/metadata/types.rs](../../src/runtime/src/metadata/types.rs) `RefKind::Stack` deref 路径不变
- [x] 5.2 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `frame_stack_depth()` callers (LoadLocalAddr) 不变

## 阶段 6: 测试

- [x] 6.1 现有 stack_trace_field e2e 双 mode 通过
- [x] 6.2 加 1-2 个 unit 测试：单一 push_frame / pop_frame；frame_state_at 返回正确指针
- [x] 6.3 加 ctor / ToString 路径 stack-trace 集成测试（新 bugfix 覆盖）

## 阶段 7: 验证

- [x] 7.1 `cargo build --manifest-path src/runtime/Cargo.toml` 全绿
- [x] 7.2 `cargo test --manifest-path src/runtime/Cargo.toml --lib` 全绿
- [x] 7.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 7.4 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 7.5 性能：1 push/1 pop 替代 3 push/3 pop；预期持平或略快

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/src/exception/mod.rs` | MODIFY | FrameInfo → VmFrame，加 regs/env_arena ptr |
| `src/runtime/src/vm_context.rs` | MODIFY | 单一 call_stack；GC scanner 单循环；新 API |
| `src/runtime/src/interp/mod.rs` | MODIFY | exec_function + FrameGuard 用新 API |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY (minor) | update_caller_line 不变签名 |
| `src/runtime/src/jit/mod.rs` | MODIFY | run_fn 用新 API |
| `src/runtime/src/jit/helpers/call.rs` | MODIFY | jit_call |
| `src/runtime/src/jit/helpers/vcall.rs` | MODIFY | jit_vcall |
| `src/runtime/src/jit/helpers/closure.rs` | MODIFY | jit_call_indirect |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | **bugfix** jit_obj_new |
| `src/runtime/src/jit/helpers/value.rs` | MODIFY | **bugfix** jit_to_str |
| `src/runtime/src/exception/tests.rs` | MODIFY | 测试 ptr field |
| `src/runtime/src/vm_context_tests.rs` (or similar) | MODIFY | 单一 push/pop 测试 |

**只读引用：**
- `metadata/types.rs` `RefKind::Stack` —— deref 路径不动；frame_state_at 仍返 regs ptr

## 备注

- **零 zbc 格式变更** — 这是纯 runtime 内部重构，无 binary 影响
- **纯运行时 invariant 加强** — 任何 invoke 路径都 push 全套（regs + env_arena + name + file），杜绝"GC 看得见但 trace 看不到"的部分帧
- **不动 interp Frame / JitFrame 结构** — 它们继续承载 regs/env_arena 的实际数据；VmFrame 只是 metadata + 指针的统一容器
