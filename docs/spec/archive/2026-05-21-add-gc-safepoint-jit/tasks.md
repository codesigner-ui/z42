# Tasks: JIT-mode GC safepoint insertion

> 状态：🟢 已完成 | 创建：2026-05-21 | 完成：2026-05-21 | 类型：vm

## 进度概览
- [x] 阶段 1: jit_check_safepoint helper 定义
- [x] 阶段 2: HelperIds + register_symbols + declare_imports
- [x] 阶段 3: translate.rs 4 个 site 插桩
- [x] 阶段 4: 集成测试
- [x] 阶段 5: 文档同步
- [x] 阶段 6: 归档 + commit + push

## 阶段 1: helper 定义

- [x] 1.1 `src/runtime/src/jit/helpers/control.rs` 加 `#[no_mangle] pub unsafe extern "C" fn jit_check_safepoint(frame, ctx)` —— 通过 `(*ctx).vm_ctx` 调用 `gc::safepoint::check_safepoint`
- [x] 1.2 cargo build (release with jit feature) GREEN

## 阶段 2: registry 注册

- [x] 2.1 `src/runtime/src/jit/helpers/registry.rs` HelperIds 加 `check_safepoint: FuncId` 字段
- [x] 2.2 `register_symbols` 加 `reg!("jit_check_safepoint", control::jit_check_safepoint)`
- [x] 2.3 `declare_imports` 加 `check_safepoint: decl!("jit_check_safepoint", [ptr, ptr], [])`
- [x] 2.4 cargo build GREEN

## 阶段 3: translate.rs 插桩

- [x] 3.1 `translate.rs` 导入 `let hr_check_safepoint = imp!(helper_ids.check_safepoint);`
- [x] 3.2 Function entry (after `builder.switch_to_block(cl_blocks[0])` + frame_val/ctx_val bind，在第一个 instruction 前)：emit `builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val])`
- [x] 3.3 Terminator::Br 当 `target ≤ block_idx`：emit check_safepoint 在 jump 之前
- [x] 3.4 Terminator::BrCond 在 brif 之前 unconditional emit check_safepoint
- [x] 3.5 `Instruction::Call` 在 throw 检查之后 emit check_safepoint
- [x] 3.6 `Instruction::CallIndirect` 在 throw 检查之后 emit check_safepoint
- [x] 3.7 cargo build (jit feature) GREEN

## 阶段 4: 集成测试

- [x] 4.1 `cross_thread_smoke.rs` 加 `jit_check_safepoint_helper_invokes_protocol`：
        直接调 `jit_check_safepoint` 验证 trampoline 转 `check_safepoint`（需要构造 JitModuleCtx — 可用最小 fixture）
- [x] 4.2 `cross_thread_smoke.rs` 加 `jit_worker_with_gc_collect_no_deadlock`：
        如果可以构造 minimal JIT function 走端到端最理想；否则用 helper-level test 兜底
- [x] 4.3 cargo test --features jit 全过
- [x] 4.4 ./scripts/test-all.sh ALL GREEN

## 阶段 5: 文档同步

- [x] 5.1 `docs/design/runtime/concurrency.md` 删除 "JIT-mode safepoint 待 add-gc-safepoint-jit"；next-step list 标 ✅
- [x] 5.2 `docs/design/runtime/vm-architecture.md` Safepoint v0 范围表 JIT 行改 ✅

## 阶段 6: 归档 + commit

- [x] 6.1 mv → `docs/spec/archive/2026-05-21-add-gc-safepoint-jit/`
- [x] 6.2 commit + push

## 备注

### 实施期发现 1 —— 集成测试 fixture overhead

阶段 4 原计划在 `cross_thread_smoke.rs` 加 integration test 直接调
`jit_check_safepoint(frame, ctx)`。但发现 `jit::helpers` 模块声明为
`pub(crate) mod helpers`，`jit::frame` 是 private `mod frame`，integration
test 是独立 crate 无法访问。

**两种修复路径**：(A) 把 helpers/frame 改为 `pub` 暴露内部 API；
(B) 在 control.rs 内嵌 `#[cfg(test)] mod check_safepoint_tests`，用
unit test 跑（同 crate 内可访问）。

选 **B**：JIT helpers 是内部 API，对外暴露破坏封装；unit test 在同模块内
直接访问 `pub unsafe extern "C" fn jit_check_safepoint` 直接调，验证
trampoline 行为。2 个 unit test 覆盖 Idle no-op + drain auto_collect。

### 实施期发现 2 —— JIT-compiled function 端到端测试 deferred

完整 "JIT worker park under GC request" 测试需 `jit::compile_module` +
load a real z42 module + spawn worker + drive collect — fixture 几百行。
v0 不实施；现 trampoline unit test + interp 同结构 cross_thread 测试
已 cover the protocol。Stdlib + test-vm.sh 现有 JIT-mode 测试不回归即
证明 translate.rs 4 个 site emit 不破坏 codegen 正确性。

后续若有 JIT-mode multi-thread bug，独立 spec `add-jit-multithread-e2e-test`
按需补 fixture。
