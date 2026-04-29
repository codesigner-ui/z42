# Tasks: Pin/Unpin runtime + PinnedView Value (C4)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 进度概览

- [x] 阶段 1: z42-abi `Z42_VALUE_TAG_PINNED_VIEW` 常量
- [x] 阶段 2: `Value::PinnedView` + PinSourceKind variant
- [x] 阶段 3: `PinPtr` runtime 实现
- [x] 阶段 4: `UnpinPtr` runtime 实现
- [x] 阶段 5: `FieldGet` 加 PinnedView.ptr/.len
- [x] 阶段 6: `marshal::value_to_z42` 加 PinnedView 分支
- [x] 阶段 7: 修各 match 漏 PinnedView 的地方（jit/translate.rs / serde / debug print）
- [x] 阶段 8: 修改 trap 测试 + 加 pin e2e 测试
- [x] 阶段 9: numz42-c 加 strlen_with_hint（可选；简单替代为 inline c-fn 验证 ptr 入 native）
- [x] 阶段 10: 文档同步
- [x] 阶段 11: 全绿验证 + 归档

---

## 阶段 1: z42-abi 常量

- [x] 1.1 修改 `crates/z42-abi/src/lib.rs`：加 `pub const Z42_VALUE_TAG_PINNED_VIEW: u32 = 8;`
- [x] 1.2 修改 `crates/z42-abi/tests/abi_layout_tests.rs`：在 `flag_bits_pinned` 同段加 `assert_eq!(Z42_VALUE_TAG_PINNED_VIEW, 8);`
- [x] 1.3 修改 `src/runtime/src/native/dispatch.rs`：re-export 或同步常量（保持与 C2 风格一致：本地 const + 与 z42-abi 对应）
- [x] 1.4 `cargo test -p z42-abi` 通过

## 阶段 2: Value variant

- [x] 2.1 修改 `src/runtime/src/metadata/types.rs`：
  - 加 `pub enum PinSourceKind { Str, ArrayU8 }`（derive Debug, Clone, Copy, PartialEq, Eq）
  - 在 `Value` enum 加 `PinnedView { ptr: u64, len: u64, kind: PinSourceKind }` variant
  - `impl PartialEq for Value` 加分支（按字段比较）
- [x] 2.2 验证编译通过；其他 match 处缺分支会报错——逐一处理见阶段 7

## 阶段 3: PinPtr runtime

- [x] 3.1 修改 `src/runtime/src/interp/exec_instr.rs::Instruction::PinPtr` 分支：
  - 取 frame.get(*src)
  - 匹配 `Value::Str(s)` → 构造 `Value::PinnedView { ptr: s.as_ptr() as u64, len: s.len() as u64, kind: Str }`
  - 匹配 `Value::Array(_)` → bail Z0908 含 "Array<u8> pinning lands in follow-up"
  - 其他 → bail Z0908 含变体名
- [x] 3.2 frame.set(*dst, view)

## 阶段 4: UnpinPtr runtime

- [x] 4.1 修改 `src/runtime/src/interp/exec_instr.rs::Instruction::UnpinPtr` 分支：
  - 取 frame.get(*pinned)
  - 匹配 `Value::PinnedView { .. }` → no-op
  - 其他 → bail Z0908

## 阶段 5: FieldGet PinnedView 分支

- [x] 5.1 找到 `exec_instr.rs::FieldGet` 的实现位置（在 dispatch.rs 或同位）
- [x] 5.2 加 PinnedView 分支：
  - field = "ptr" → `Value::I64(view.ptr as i64)`
  - field = "len" → `Value::I64(view.len as i64)`
  - 其他 → bail Z0908 含字段名

## 阶段 6: marshal PinnedView 分支

- [x] 6.1 修改 `src/runtime/src/native/marshal.rs::value_to_z42`：
  - `(Value::PinnedView { ptr, .. }, SigType::Ptr | SelfRef | CStr)` → `dispatch::z42_native_ptr(ptr as *mut c_void)`
  - `(Value::PinnedView { len, .. }, SigType::U64 | I64 | U32 | I32 | usize 等)` → `dispatch::z42_i64(len as i64)`
  - 其他 PinnedView × 不兼容 SigType → 现有 catchall (含 "blittable" 字符串)
- [x] 6.2 添加 marshal_tests 单元测试：3 个 case（ptr / len / 不兼容）

## 阶段 7: match 漏 PinnedView 的地方

- [x] 7.1 grep `match.*Value\b` 在 `src/runtime/src/` 内
- [x] 7.2 处理每个 match：
  - 算术 / 比较 dispatch 处 — 一律 bail "PinnedView 不参与算术"
  - GC trace / heap roots — PinnedView 内无 GcRef，无需 trace
  - serde / debug print — derive 应自动；若有 manual impl 加分支
  - JIT translate.rs::max_reg — 已经覆盖 dst （PinPtr/UnpinPtr 在 C1 加过）
  - JIT translate.rs::translate_function — 已 bail，不需要新增
  - corelib / convert.rs::value_to_str — 加 "<PinnedView ptr=… len=…>" debug rendering
  - obj_to_string / format / collect_args — 看场景

## 阶段 8: 测试

- [x] 8.1 修改 `src/runtime/tests/native_opcode_trap.rs`：
  - `pin_ptr_traps_with_spec_pointer` → 改为 `pin_ptr_str_returns_pinned_view`：构造 ConstStr → PinPtr → 验 dst 是 PinnedView
  - `unpin_ptr_traps_with_spec_pointer` → 改为 `unpin_ptr_pinned_view_succeeds`：先 PinPtr 再 UnpinPtr 不报错
  - 加 `pin_ptr_non_str_z0908`：源为 I64 时报 Z0908
  - 加 `unpin_ptr_non_view_z0908`：参数不是 PinnedView 时报 Z0908
- [x] 8.2 创建 `src/runtime/tests/native_pin_e2e.rs`：
  - `pin_view_field_access`：Pin → FieldGet "ptr" → 是非 0；FieldGet "len" → 等于字符串长度
  - `pin_view_unknown_field_z0908`
  - `pin_array_z0908`
- [x] 8.3 运行所有 tests 通过

## 阶段 9: numz42-c c_strlen（可选）

- [x] 9.1 决策：如果加 strlen 让 e2e 测试更"真"，加；否则 step 8.2 已足够覆盖 C4 范围
- [x] 9.2 (如做) 修改 `src/runtime/tests/data/numz42-c/numz42.c`：加 `c_strlen` 函数 + 加进 method 表
- [x] 9.3 (如做) `tests/native_pin_e2e.rs` 加端到端 case：String → pin → CallNative strlen → 期望长度

## 阶段 10: 文档同步

- [x] 10.1 修改 `docs/design/error-codes.md` Z0908 → 已启用 + 3 个抛出条件
- [x] 10.2 修改 `docs/design/interop.md` §6.3 描述 PinnedView Value + Z42_VALUE_TAG_PINNED_VIEW = 8
- [x] 10.3 修改 `docs/design/ir.md` PinPtr/UnpinPtr 段：runtime 语义、PinnedView 形状
- [x] 10.4 修改 `docs/design/vm-architecture.md` Value 变体段加 PinnedView 行
- [x] 10.5 修改 `docs/roadmap.md` Native Interop 表 C4 → ✅
- [x] 10.6 修改 `src/runtime/src/native/README.md` 状态部分加 C4 进展

## 阶段 11: 全绿 + 归档

- [x] 11.1 `cargo build --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 11.2 `cargo test --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 11.3 `dotnet build src/compiler/z42.slnx` + `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [x] 11.4 `./scripts/test-vm.sh`
- [x] 11.5 输出验证报告
- [x] 11.6 spec scenarios 1:1 对照
- [x] 11.7 归档 spec/changes/impl-pinned-block → spec/archive/2026-04-29-impl-pinned-block
- [x] 11.8 commit + push

## 备注

- C4 只动 Rust VM；C# 编译器零变更。z42 用户代码 `pinned` 关键字 / 语法在 C5 一并落地
- numz42-c PoC 是否扩展看 step 9 决策；不影响 spec 验证完整性
- Value 加新 variant 通常会触碰多处 match —— 阶段 7 是最容易遗漏的工作，重点 grep
