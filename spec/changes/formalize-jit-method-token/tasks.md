# Tasks: formalize-jit-method-token

> 状态：🟡 进行中（Phase 2.A + 2.B + 2.C 已落地）| 创建：2026-05-08
> 类型：vm（runtime JIT helper 接口契约扩展，refactor 性质）
> 来源：Sibling spec to `2026-05-08-introduce-method-token` Phase 2 follow-up

**目标**: 把 JIT helpers 从字符串身份（name_ptr / name_len 二元组）切换到
`introduce-method-token` Phase 1 引入的 token newtype（`MethodId` /
`BuiltinId` / `StaticFieldId` / `TypeId` / `FieldId` / vtable slot），
让 JIT 热路径享受同款 token cache 收益（interp 已落地）。

JIT 编译在 `Vm::run` 中（resolver 之后）触发，所以 `Function.resolved`
在 `jit::compile_module` 进入时已就绪——translate.rs codegen 直接读
ResolvedTokens 并 emit `iconst.i32 <id>` 作 helper 实参。

---

## 进度（2026-05-08）

✅ **Phase 2.A — JIT Builtin** (`1467449`):
- `jit_builtin` 签名改为 `(frame, ctx, dst, builtin_id, args_ptr, argc) -> u8`
- 弃 name_ptr/len，helper 直接 `BUILTINS[id]` 查找
- translate.rs Builtin codegen 读 `Function.resolved.builtin_tokens` emit iconst
- registry.rs Cranelift 签名同步更新
- 验证: cargo test + VM golden 310/310 全绿

✅ **Phase 2.B — JIT Static{Get,Set}** (`ffa29f0`):
- `jit_static_get` / `jit_static_set` 签名 `(field_ptr, field_len)` → `field_id: u32`
- helper 走 `static_get_by_id` / `static_set_by_id`
- translate.rs 新增 `static_field_id_at(func, block, instr, _field)` 辅助；StaticGet/StaticSet codegen emit iconst.i32 of id
- 验证: cargo test + VM golden 310/310 全绿

✅ **Phase 2.C — JIT Call** (本次):
- `FnEntry` 加 `#[derive(Copy, Clone)]`（允许 HashMap + Vec 双驻留）
- `JitModuleCtx` 加 `fn_entries_by_id: Vec<Option<FnEntry>>`（索引 = MethodId.0；`None` slot 留给未来 abstract / extern stub）
- `compile_module` 同时 push 两个表（HashMap 按 name 索引，Vec 按 module.functions 顺序）
- `jit_call` 签名: `(frame, ctx, dst, method_id, fn_name_ptr, fn_name_len, args_ptr, argc) -> u8` —— hot path 直接 Vec[id]，UNRESOLVED 时走 name fallback（cross-zpkg）
- translate.rs `method_id_at(func, block, instr) -> u32` helper；Call codegen emit `iconst.i32 <method_id>` 作 helper 第 4 参数（在 dst 之后、name 之前）
- registry.rs Cranelift 签名: `[ptr, ptr, i32t, i32t, ptr, i64t, ptr, i64t]`
- 验证: cargo test + VM golden 310/310 全绿

🟡 **Phase 2.D — JIT ObjNew** (待启动):
- TypeId 主要 observability 用途（type_registry 仍 HashMap）；可推迟到 Phase 3 zbc 格式 bump 时一起做
- 工作量: ~30min

🟡 **Phase 2.E — JIT VCall + Field IC** (待启动，最复杂):
- IC 集成：JIT 机器码需要 inline 检查 `cached_type_id == receiver.type_desc.id` 后直接 `module.functions[cached_fn_idx]`
- 三选一:
  - A. 助手承担 IC（helper 接 IC 指针，dispatch 仍 helper-call 一次）—— 简单但失去内联收益
  - B. JIT 在机器码 emit IC check + branch + slow-path call —— 收益最大但复杂
  - C. 不做 IC（跟现状一样字符串路径）—— 放弃热路径优化
- 推荐 A（与 interp 行为对齐 + 助手已实现 IC 逻辑）
- 工作量: ~2h

---

## 后续 Phase（独立 spec）

- **Phase 3**: zbc 格式 bump，IR struct 字段从 String 改 u32 token (整体 compiler+VM 双端联动)
- **Phase 4**: Compiler 端 token-aware emit（不再产 string 中间形态）

---

## 实施备注

JitModuleCtx restructure for jit_call:
```rust
pub struct JitModuleCtx {
    pub string_pool: Vec<String>,
    pub fn_entries: HashMap<String, FnEntry>,         // existing — name fallback
    pub fn_entries_by_id: Vec<Option<FnEntry>>,        // NEW — MethodId.0 → FnEntry
                                                       // (Option because some MethodIds may
                                                       //  not have JIT-compiled bodies, e.g.
                                                       //  abstract methods)
    pub module: *const Module,
    pub vm_ctx: *mut VmContext,
}
```

Build site: `jit/mod.rs::compile_module` 末尾构造 fn_entries_vec 时同时 push 进 fn_entries_by_id（按 module.functions 顺序，与 MethodId 一致）。

ObjNew 与 VCall 因为 receiver-runtime-type 决定，不能纯 codegen 时取 token；需要 helper 中检查 IC（与 interp 一致）。
