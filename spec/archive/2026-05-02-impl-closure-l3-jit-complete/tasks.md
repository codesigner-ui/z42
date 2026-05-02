# Tasks: 补完 JIT 闭包指令翻译 (impl-closure-l3-jit-complete)

> 状态：🟢 已完成 | 创建：2026-05-02 | 归档：2026-05-02 | 类型：vm（实施变更）

## Scope 偏离记录

实施期间 1 处简化（向好方向）：

1. **不需要 `Box::leak`**：design 中规划用 `leak_str` / `leak_regs` 静态化字符串和
   寄存器索引数组。实施时发现 translate.rs 已有 `str_val!` / `regs_val!` 宏直接
   从 `Instruction::Call` 等指令的 String / Vec<u32> 字段取指针——这些字段属于
   `Module` 结构，生命周期 ≥ JitModule，无需额外 leak。直接复用宏，三条 closure
   指令翻译总共只用 ~20 行。

## 进度概览
- [x] 阶段 1: helpers_closure.rs 实现 3 个 helper
- [x] 阶段 2: HelperIds 扩展 + mod.rs 注册
- [x] 阶段 3: translate.rs 三条指令翻译（替换 bail!）
- [x] 阶段 4: Golden 解锁（删 4 个 interp_only）+ JIT 模式验证
- [x] 阶段 5: GREEN + 归档

## 阶段 1: helpers_closure.rs 实现

- [x] 1.1 新建 `src/runtime/src/jit/helpers_closure.rs` 含模块头注释
- [x] 1.2 实现 `jit_load_fn(frame, ctx, dst, name_ptr, name_len) -> u8`
  - 解 frame，构造 Value::FuncRef，写入 frame.regs[dst]
- [x] 1.3 实现 `jit_mk_clos(frame, ctx, dst, name_ptr, name_len, caps_ptr, caps_len) -> u8`
  - 遍历 captures，clone frame.regs[*r]
  - vm_ctx.heap().alloc_array(env_vec) → Value::Array(rc)
  - 构造 Value::Closure { env: rc, fn_name }
  - 写入 frame.regs[dst]
- [x] 1.4 实现 `jit_call_indirect(frame, ctx, dst, callee, args_ptr, args_len) -> u8`
  - match callee Value variant：FuncRef 直调；Closure prepend env
  - lookup ctx.fn_entries[fn_name]
  - 创建 callee frame，push_frame_regs，调用 JitFn，pop_frame_regs
  - 处理异常返回 1；正常 frame.regs[dst] = ret，返回 0
- [x] 1.5 编译验证：`cargo build` 0 错误

## 阶段 2: HelperIds 扩展 + mod.rs 注册

- [x] 2.1 `helpers.rs::HelperIds`：加 `load_fn`, `mk_clos`, `call_indirect: FuncId`
- [x] 2.2 `mod.rs::compile_module`：添加 `mod helpers_closure;`
- [x] 2.3 `mod.rs::compile_module`：jit_builder.symbol(...) 注册 3 个符号
- [x] 2.4 `translate.rs::declare_helpers`：扩展返回值结构 + 调 `jit.declare_function(...)` 三次
- [x] 2.5 编译验证

## 阶段 3: translate.rs 三条指令翻译

- [x] 3.1 添加 `leak_str(s: &str) -> (i64, i64)` 工具函数
- [x] 3.2 添加 `leak_regs(rs: &[u32]) -> (i64, i64)` 工具函数
- [x] 3.3 替换 `Instruction::LoadFn` bail 为 helper 调用
  - leak_str(func) + Cranelift iconst → call helper.load_fn → 检查返回码
- [x] 3.4 替换 `Instruction::MkClos` bail 为 helper 调用
  - leak_str(fn_name) + leak_regs(captures) → call helper.mk_clos → 检查返回码
- [x] 3.5 替换 `Instruction::CallIndirect` bail 为 helper 调用
  - leak_regs(args) → call helper.call_indirect → 检查返回码
- [x] 3.6 编译验证 `cargo build` 0 错误

## 阶段 4: Golden 解锁 + JIT 验证

- [x] 4.1 删除 `src/runtime/tests/golden/run/lambda_l2_basic/interp_only`
- [x] 4.2 删除 `src/runtime/tests/golden/run/local_fn_l2_basic/interp_only`
- [x] 4.3 删除 `src/runtime/tests/golden/run/closure_l3_capture/interp_only`
- [x] 4.4 删除 `src/runtime/tests/golden/run/closure_l3_loops/interp_only`
- [x] 4.5 `bash scripts/test-vm.sh interp` 全绿
- [x] 4.6 `bash scripts/test-vm.sh jit` 全绿（关键）
- [x] 4.7 `bash scripts/test-vm.sh`（默认两模式）全绿

## 阶段 5: GREEN + 归档

- [x] 5.1 `dotnet build` / `cargo build`：0 错误 0 警告
- [x] 5.2 `dotnet test`：100% 通过（无新增 C# 测试）
- [x] 5.3 `./scripts/test-vm.sh`：100% 通过（interp + jit 全部 closure golden）
- [x] 5.4 `docs/roadmap.md` L3-C2-jit 标 ✅
- [x] 5.5 移到 `spec/archive/2026-05-02-impl-closure-l3-jit-complete/` + commit + push

## 备注

实施过程中需要特别留意：

- **Helper 签名一致性**：严格遵循 `extern "C" fn(*mut JitFrame, *const JitModuleCtx, ...) -> u8` 模式。所有现有 helper（jit_call / jit_obj_new / jit_vcall）都是这套约定；不要发明新模式。
- **GC root 注册成对**：每个调用 callee 的 helper（即 jit_call_indirect）都必须配对 `push_frame_regs` / `pop_frame_regs`。漏掉 pop 会泄漏 GC root；漏掉 push 会让 callee 内分配的对象意外回收。
- **Box::leak 模块级永久泄漏**：所有 leak 的 String / Vec<u32> 在 JIT 模块生命周期内都不应释放（因为 JIT 编译的代码会引用其指针）。当前 JitModule 是模块级长寿对象——可接受。
- **CallIndirect 返回值复制**：callee frame.regs[0] 是 callee 的返回值；helper 必须将其复制到 caller frame.regs[dst]。这与 jit_call 的处理一致——按既有模式抄。
- **异常路径**：helper 内任何错误（callee not found、type mismatch、etc.）都通过 `set_pending_exception(vm_ctx, err)` 投递，然后 helper 返回 1。translate.rs 端的 `install_exception_check` 在返回非 0 时跳到 catch / ret 1。
