# Tasks: extend-jit-helper-abi

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-28
> 类型：vm（完整流程；按用户要求"直接完整的"，省略阶段 6.5 单独 gate；
> 实施 + 验证 + 归档一体）

## 完成备注

- ABI 一次到位：所有 37 个 extern "C" helper 签名加 `ctx: *const JitModuleCtx`
  第 2 参（含 macro 系列 arith_op! / cmp_op! / bitwise_op!）；translate.rs
  declare_helpers ~32 处 + 调用点 30+ 处同步插 ctx ptr / ctx_val
- 删除 jit/helpers.rs 内 PENDING_EXCEPTION + STATIC_FIELDS thread_local +
  sync_in_from_ctx / sync_out_to_ctx 函数；jit/mod.rs 同步删调用
- 新增 `vm_ctx_ref(ctx) -> &VmContext` 工具收口 unsafe 边界
- helpers_object.rs jit_static_get / jit_static_set 直接 ctx 调用，不再
  走 helpers.rs wrapper（dead code 删除）
- 至此 review2 §3 完整目标达成：runtime 仅余 1 个 thread_local
  （jit/frame.rs::FRAME_POOL，pure allocator cache）
- 验证：cargo test 68+4 ✅ / scripts/test-vm.sh 200/200 ✅ /
  scripts/test-cross-zpkg.sh 1/1 ✅ / dotnet test 734/734 ✅

## 阶段 1：helpers.rs 顶层改造

- [x] 1.1 删除 `PENDING_EXCEPTION` thread_local
- [x] 1.2 删除 `STATIC_FIELDS` thread_local
- [x] 1.3 删除 `sync_in_from_ctx` / `sync_out_to_ctx` 函数
- [x] 1.4 改 `set_exception(v)` → `set_exception(ctx: &VmContext, v)`
- [x] 1.5 改 `take_exception()` → `take_exception(ctx: &VmContext)`
- [x] 1.6 改 `take_exception_error()` → `take_exception_error(ctx: &VmContext)`
- [x] 1.7 改 `static_get(field)` → `static_get(ctx: &VmContext, field)`
- [x] 1.8 改 `static_set_inner(field, val)` → `static_set_inner(ctx, field, val)`
- [x] 1.9 删除 `static_fields_clear()`（用 `ctx.static_fields_clear()` 替代）
- [x] 1.10 新增 `pub(super) unsafe fn vm_ctx_ref<'a>(jit_ctx) -> &'a VmContext`

## 阶段 2：helpers_arith.rs ABI 改造

- [x] 2.1 jit_add 签名加 ctx；set_exception 传 ctx
- [x] 2.2 arith_op! macro 改造（一改全改 sub/mul/div/rem）
- [x] 2.3 jit_eq / jit_ne 签名加 ctx（无 set_exception 但保持一致）
- [x] 2.4 cmp_op! macro 改造（lt/le/gt/ge）
- [x] 2.5 jit_and / jit_or / jit_not / jit_neg 签名加 ctx
- [x] 2.6 bitwise_binop! macro 改造（bit_and/or/xor/shl/shr）
- [x] 2.7 jit_bit_not 签名加 ctx

## 阶段 3：helpers_mem.rs ABI 改造

- [x] 3.1 jit_const_i32 / i64 / f64 / bool / char / null：签名加 ctx
- [x] 3.2 jit_const_str：保留（已带 ctx）
- [x] 3.3 jit_copy 签名加 ctx
- [x] 3.4 jit_str_concat 签名加 ctx；set_exception 传 ctx
- [x] 3.5 jit_to_str：保留（已带 ctx）；内部 set_exception 改传 ctx
- [x] 3.6 jit_get_bool 签名加 ctx
- [x] 3.7 jit_set_ret 签名加 ctx
- [x] 3.8 jit_throw 签名加 ctx；通过 ctx 调 set_exception
- [x] 3.9 jit_install_catch 签名加 ctx

## 阶段 4：helpers_object.rs ABI 改造

- [x] 4.1 jit_call：保留（已带 ctx）；内部 set_exception 改传 ctx
- [x] 4.2 jit_builtin 签名加 ctx；set_exception 传 ctx
- [x] 4.3 jit_array_new / array_new_lit / array_get / array_set / array_len
      签名加 ctx；set_exception 传 ctx
- [x] 4.4 jit_obj_new：保留（已带 ctx）；内部 set_exception 改传 ctx
- [x] 4.5 jit_field_get / field_set 签名加 ctx；set_exception 传 ctx
- [x] 4.6 jit_vcall：保留（已带 ctx）；内部 set_exception 改传 ctx
- [x] 4.7 jit_is_instance / as_cast：保留（已带 ctx）
- [x] 4.8 jit_static_get 签名加 ctx；改用 vm_ctx_ref(ctx).static_get()
- [x] 4.9 jit_static_set 签名加 ctx；改用 vm_ctx_ref(ctx).static_set()

## 阶段 5：translate.rs 同步

- [x] 5.1 `declare_helpers` 内 ~24 个 `decl!` 在第 1 个 ptr 后插入第 2 个 ptr
      （已带 ctx 的 6 个不变）
- [x] 5.2 helper call 位点 ~30 处插入 `ctx_val` 第 2 参

## 阶段 6：jit/mod.rs 清理

- [x] 6.1 删除 `sync_in_from_ctx(ctx)` / `sync_out_to_ctx(ctx)` 调用
- [x] 6.2 `use helpers::{...}` 移除 sync_in/sync_out
- [x] 6.3 `take_exception_error()` 调用改为 `take_exception_error(ctx)`

## 阶段 7：文档同步

- [x] 7.1 `exception/mod.rs` 顶部注释更新（runtime 已无 thread_local 异常状态）
- [x] 7.2 `thread/mod.rs` 顶部注释更新（仅 FRAME_POOL 保留）
- [x] 7.3 `docs/design/vm-architecture.md` 删 sync 桥接段；说明 helper ABI
      已扩展

## 阶段 8：验证

- [x] 8.1 `cargo build --manifest-path src/runtime/Cargo.toml` 0 错 0 warn
- [x] 8.2 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（68+4）
- [x] 8.3 `./scripts/test-vm.sh` 200/200 全绿
- [x] 8.4 `./scripts/test-cross-zpkg.sh` 全绿
- [x] 8.5 `dotnet build src/compiler/z42.slnx` 通过
- [x] 8.6 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 8.7 spec scenario 逐条核对

## 阶段 9：归档

- [x] 9.1 移动 `spec/changes/extend-jit-helper-abi/` →
      `spec/archive/2026-04-28-extend-jit-helper-abi/`
- [x] 9.2 自动 commit + push（含 .claude/ 与 spec/）
