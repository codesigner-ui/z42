# Spec: jit-helper-abi

## ADDED Requirements

### Requirement: 所有 JIT extern "C" helper 接 `*const JitModuleCtx` 第 2 参

#### Scenario: helper 签名一致性

- **WHEN** 检查 `jit/helpers_arith.rs` / `helpers_mem.rs` / `helpers_object.rs`
- **THEN** 所有 `pub unsafe extern "C" fn jit_*` 函数签名首两个参数为
  `frame: *mut JitFrame, ctx: *const JitModuleCtx`
- **AND** translate.rs 内 `declare_helpers` 中所有 `decl!` 第一个 ptr 后立刻
  跟第 2 个 ptr（ctx）

#### Scenario: helper 调用点同步

- **WHEN** 检查 translate.rs 内 `builder.ins().call(hr_*, &[...])` 调用
- **THEN** 每个调用的参数列表第 1 个是 `frame_val`，第 2 个是 `ctx_val`，
  之后才是 helper 自己的参数

### Requirement: 删除 JIT 端 thread_local

#### Scenario: jit/helpers.rs 不再含 thread_local

- **WHEN** 检查 `jit/helpers.rs` 源码
- **THEN** 不再有 `thread_local! { ... PENDING_EXCEPTION ... }` 声明
- **AND** 不再有 `thread_local! { ... STATIC_FIELDS ... }` 声明

#### Scenario: VmContext 是规范来源

- **WHEN** 一个 JIT helper 调用 `set_exception(value)` 报告异常
- **THEN** value 直接写入 `(*ctx).vm_ctx` 指向的 `VmContext.pending_exception`
  字段，无 thread_local 中转
- **AND** `JitModule::run` 退出时 ctx.take_exception() 直接拿到该值，无需
  sync 操作

#### Scenario: 静态字段读写通过 ctx

- **WHEN** JIT-emitted 代码访问用户类 static 字段（StaticGet / StaticSet 指令）
- **THEN** `jit_static_get` / `jit_static_set` 通过 `(*ctx).vm_ctx` 调
  `VmContext.static_get/set` 方法，与 interp 端读写同一份 `static_fields`
  HashMap

### Requirement: 删除 sync 桥接代码

#### Scenario: JitModule::run 简化

- **WHEN** 检查 `JitModule::run` / `JitModule::run_fn` 源码
- **THEN** 不再调用 `sync_in_from_ctx` / `sync_out_to_ctx`
- **AND** `helpers.rs::sync_in_from_ctx` / `sync_out_to_ctx` /
  `static_fields_clear` 函数体不存在
- **AND** 进入 / 退出 entry 仅维护 `self.ctx.vm_ctx` 字段（指针写入和置空）

### Requirement: 多 VmContext 实例并发可行性提升

#### Scenario: 同线程多 ctx 顺序运行不污染（无需 sync）

- **WHEN** 同一线程依次创建 `ctx1` / `ctx2`，分别用 `Vm.run(&mut ctx, ...)`
  跑 JIT 模式的同一份 module
- **THEN** ctx1 的 static_fields / pending_exception 与 ctx2 完全隔离，**且
  无任何 thread_local 中介**
- **AND** sync 同步成本（HashMap clone）消失

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IRGen — 不受影响
- [ ] VM interp — 不受影响（依然走 ExecOutcome::Thrown，无 thread_local）
- [ ] VM JIT — **helper ABI 全面扩展**：30+ helper sig 加 ctx；translate.rs
      30+ call site 同步；jit/mod.rs sync 桥接代码删除

## IR Mapping

无 IR 变化。zbc 二进制格式不变。Cranelift 内 helper FuncId 签名变化为内部
实现细节，不暴露给用户。
