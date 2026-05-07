# Proposal: expand-jit-type-args（JIT-mode type_args propagation）

## Why

D-8b-3 Phase 2（`spec/archive/2026-05-07-add-default-generic-typeparam/`）让泛型 type-param `default(T)` 通过运行时 `instance.type_args` 解析到具体 zero-value。落地时 JIT 路径上的 `jit_obj_new` 没扩展签名，JIT-allocated 实例 `type_args` 字段始终为空 → `default(T)` 在 JIT 模式下退化为 `Value::Null`，与 interp 模式不一致。

直接结果：5 个 golden test 必须带 `interp_only` 标记跳过 JIT 模式验证：

- `src/tests/operators/default_generic_param/`
- `src/tests/operators/default_generic_param_pair/`
- `src/tests/operators/default_generic_param_field_init/`
- `src/tests/delegates/multicast_func_aggregate/`
- `src/tests/delegates/multicast_predicate_aggregate/`

design 承诺（K8 完整语义、Phase 2 Scenario "Foo<int> instance returns 0 for default(R)"）在 JIT 模式不达标。本变更补齐：让 JIT 路径与 interp 路径行为一致。

## What Changes

- `jit_obj_new` JIT helper 签名扩展：增 `type_args_ptr: *const TypeArgEntry` + `type_args_count: usize` 两参数（与 args/captures 等现有字符串数组 marshal 模式一致：连续指针对 + 长度 pair）
- `jit/translate.rs` `Instruction::ObjNew` 翻译：marshal `type_args: Vec<String>` 到 stack-allocated 字符串数组，传给 helper
- `jit_obj_new` body 在 alloc_object 后执行 `if type_args_count > 0 { rc.borrow_mut().type_args = collected; }`，与 interp 路径镜像
- 移除 5 个测试的 `interp_only` 标记，使 JIT 模式下也能通过

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | `jit_obj_new` 增 type_args 参数 + populate instance.type_args |
| `src/runtime/src/jit/translate.rs` | MODIFY | `obj_new` helper 声明的 ABI 签名 + ObjNew 翻译时 marshal type_args |
| `src/tests/operators/default_generic_param/interp_only` | DELETE | JIT 路径打通后不再需要标记 |
| `src/tests/operators/default_generic_param_pair/interp_only` | DELETE | 同上 |
| `src/tests/operators/default_generic_param_field_init/interp_only` | DELETE | 同上 |
| `src/tests/delegates/multicast_func_aggregate/interp_only` | DELETE | 同上 |
| `src/tests/delegates/multicast_predicate_aggregate/interp_only` | DELETE | 同上 |
| `docs/design/ir.md` | MODIFY | `obj_new` 0.9 版描述同步：JIT 路径现也 propagate type_args |
| `docs/deferred.md` | MODIFY | D-8b-3 Phase 2 备注："JIT 路径限制" 段更新为已解决 |

**只读引用**：

- `src/runtime/src/interp/exec_instr.rs` ObjNew dispatch — 镜像目标
- `src/runtime/src/jit/translate.rs` 现有 `regs_val!` / args marshal 模式 — type_args marshal 参考
- `src/runtime/src/metadata/types.rs` `ScriptObject.type_args` — 写入目标

## Out of Scope

- `jit_default_of` helper 已存在并工作；不动
- AOT 路径 type_args propagation（AOT 未实现，整体 placeholder）
- 进一步的方法级 type-param / free generic function 的 calling convention 携带 type_args（独立 spec `add-method-level-type-args`）

## Open Questions

- [ ] String marshal 用 length-prefixed 平面数组（每个字符串 ptr + len），还是新结构 `TypeArgEntry { ptr: *const u8, len: usize }`？参考现有 helpers 模式
- [ ] 字符串生命周期：JIT 调用时 type_args strings 来自 `Instruction::ObjNew { type_args: Vec<String> }`（持有于 module.functions 内），符号 ABI 要求 `'static` 引用还是 caller-owned？走法 `frame_ref` 间接读 String — 跨 ABI 风险评估
