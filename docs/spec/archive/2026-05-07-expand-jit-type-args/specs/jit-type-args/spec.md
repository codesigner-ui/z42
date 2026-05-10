# Spec: jit-type-args

## ADDED Requirements

### Requirement: JIT-compiled `obj.new` 写入 instance.type_args

#### Scenario: 泛型类实例化在 JIT 模式下携带 type_args

- **WHEN** `new Foo<int>()` 在 JIT-compiled 函数内执行
- **THEN** allocate 后的 `ScriptObject.type_args == ["int"]`，与 interp 路径完全一致

#### Scenario: 多 type-arg 顺序保持

- **WHEN** `new Pair<int, string>()` 在 JIT 模式
- **THEN** `instance.type_args == ["int", "string"]`，第 0 位是 K，第 1 位是 V

#### Scenario: 非泛型类零开销

- **WHEN** `new Point()`（非泛型）在 JIT 模式
- **THEN** `instance.type_args` 为空 `vec![]`，不做无谓 marshal / 写入

### Requirement: 现有 5 个 generic-T golden 在 JIT 模式下也通过

#### Scenario: 移除 interp_only 标记

- **WHEN** `./scripts/test-vm.sh` 同时跑 interp + jit 模式
- **THEN** `default_generic_param/`、`default_generic_param_pair/`、`default_generic_param_field_init/`、`multicast_func_aggregate/`、`multicast_predicate_aggregate/` 都在 JIT 模式输出与 interp 模式相同的 expected_output.txt

## MODIFIED Requirements

### Requirement: jit_obj_new helper

**Before**: `jit_obj_new(frame, ctx, dst, cls_ptr, cls_len, ctor_ptr, ctor_len, args_ptr, argc) -> u8`，alloc 后实例 `type_args` 总为空。

**After**: 同签名 + 增 2 参数 `type_args_ptr: *const String, type_args_count: usize` 末尾；alloc 后若 `type_args_count > 0` 则 `rc.borrow_mut().type_args` 写入解构出的字符串列表。

## IR Mapping

不引入新 IR 指令。复用现有 `Instruction::ObjNew { type_args: Vec<String> }`（D-8b-3 Phase 2 已加）；JIT 翻译消费这个字段。

## Pipeline Steps

- [x] Lexer / Parser / TypeChecker — 不变
- [x] IR Codegen — 不变（IR ObjNew 已携带 type_args）
- [ ] VM JIT — 扩 `jit_obj_new` 签名 + 翻译时 marshal type_args
- [x] VM interp — 不变（已 land）
