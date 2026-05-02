# Proposal: 补完 JIT 闭包指令翻译 (impl-closure-l3-jit-complete)

## Why

`impl-closure-l3-core` 落地后，闭包在 interp 模式端到端可用，但 JIT 后端
仍对 `LoadFn` / `CallIndirect` / `MkClos` 三条指令 `bail!`，所有用到闭包
的 golden test 都标了 `interp_only` 跳过 JIT。

不做会怎样：
- closure_l3_capture / closure_l3_loops / lambda_l2_basic / local_fn_l2_basic
  四个 golden 永远跑不到 JIT 路径，回归保护薄
- 实际部署若启用 JIT 模式，所有闭包代码失败编译（`bail!`）
- "L3 完整闭包"未达完整状态

本变更**端到端补完 JIT 三条指令翻译**，使所有闭包 golden 移除 `interp_only` 标记。

## What Changes

JIT 翻译三条指令（行为契约与 interp 完全一致，由 archived
`impl-closure-l3-core/specs/closure-l3-core/spec.md` L3-C-2/L3-C-5/L3-C-6 锁定）：

- **LoadFn** → 内联 helper 调用，把 `Value::FuncRef(name)` 写入目标 reg
- **MkClos** → helper 中遍历 capture regs、`heap.alloc_array(env_vec)`、构造
  `Value::Closure { env, fn_name }`、写入目标 reg
- **CallIndirect** → helper 中 match callee variant：
  - `FuncRef(name)` → 直接调用
  - `Closure { env, fn_name }` → 把 env 作为首参 prepend，再调用

不实现：
- 档 B 单态化优化
- 档 A 栈分配优化

### Pipeline 改动
- **Rust JIT**：
  - `helpers_closure.rs`：新建，3 个 `extern "C"` helper
  - `helpers.rs`：`HelperIds` struct 加 3 个 FuncId 字段
  - `translate.rs::declare_helpers`：注册 3 个 helper 符号
  - `translate.rs::translate_instr`：3 条指令 bail 改为 helper 调用
  - `mod.rs::compile_module`：添加 helpers_closure 模块；注册 helper 符号
- **Golden tests**：4 个 closure golden 删除 `interp_only` 标记
- **No new opcodes / Value variants / IR changes**

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/jit/helpers.rs` | MODIFY | `HelperIds` 加 3 个 FuncId 字段 |
| `src/runtime/src/jit/helpers_closure.rs` | NEW | 3 个 `extern "C"` helper |
| `src/runtime/src/jit/mod.rs` | MODIFY | 添加 helpers_closure 模块；注册 helper 符号 |
| `src/runtime/src/jit/translate.rs` | MODIFY | declare_helpers + translate_instr 三条指令 |
| `src/runtime/tests/golden/run/lambda_l2_basic/interp_only` | DELETE | JIT 已支持 |
| `src/runtime/tests/golden/run/local_fn_l2_basic/interp_only` | DELETE | JIT 已支持 |
| `src/runtime/tests/golden/run/closure_l3_capture/interp_only` | DELETE | JIT 已支持 |
| `src/runtime/tests/golden/run/closure_l3_loops/interp_only` | DELETE | JIT 已支持 |
| `docs/roadmap.md` | MODIFY | L3-C2-jit 标 ✅ |
| `spec/changes/impl-closure-l3-jit-complete/{proposal,design,tasks}.md` + specs/ | NEW | 本变更规范 |

**只读引用**：
- `src/runtime/src/interp/exec_instr.rs` — interp 行为参考（逐行移植）
- `src/runtime/src/jit/helpers_object.rs::jit_call` — helper 模式参考

## Out of Scope

- ❌ 档 B 单态化（`impl-closure-l3-monomorphize`）
- ❌ 档 A 栈分配（`impl-closure-l3-escape-stack`）
- ❌ Send 派生（与 concurrency 同期）

## Open Questions

- [ ] `helpers_closure.rs` 单文件 vs 加到 `helpers_object.rs`？倾向新建（闭包是独立子系统，便于后续 monomorphize/stack 拓展）
- [ ] `jit_load_fn` 是否需要 helper 包装？或可直接在 translate.rs 内联？倾向 helper（一致性 > 微优化）
