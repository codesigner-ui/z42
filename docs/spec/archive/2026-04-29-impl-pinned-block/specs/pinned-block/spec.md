# Spec: Pin/Unpin runtime + PinnedView Value (C4)

> C4 仅做 VM runtime 接通；z42 源代码 `pinned` 块语法在 C5 引入。本 spec 通过手工 IR 验证。

## ADDED Requirements

### Requirement: PinPtr produces PinnedView from String

#### Scenario: 正常 pin String
- **WHEN** 字节码执行 `PinPtr dst src`，`src` 持有 `Value::Str("hello".to_string())`
- **THEN** `dst` 变为 `Value::PinnedView { ptr: <非 0>, len: 5, kind: Str }`

#### Scenario: PinPtr 非可 pin 类型 → Z0908
- **WHEN** `src` 持有 `Value::I64(42)` / `Value::Bool(true)` / `Value::Object(...)` 等
- **THEN** VM 返回 `Err`，message 含 `"Z0908"` + 类型描述

#### Scenario: PinPtr Array<Value> → Z0908 (C4 限制)
- **WHEN** `src` 持有 `Value::Array(...)`（z42 通用数组）
- **THEN** VM 返回 `Err`，message 含 `"Z0908"` + 指明"Array<u8> pinning lands in follow-up"

---

### Requirement: UnpinPtr accepts PinnedView and is no-op

#### Scenario: UnpinPtr PinnedView 成功
- **WHEN** `pinned` 寄存器持有 `Value::PinnedView { ... }`
- **THEN** VM 不改变 frame；不报错

#### Scenario: UnpinPtr 非 PinnedView → Z0908
- **WHEN** `pinned` 持有 `Value::I64(0)` / `Value::Null` 等
- **THEN** VM 返回 `Err`，message 含 `"Z0908"` + `"PinnedView"`

---

### Requirement: PinnedView field access through FieldGet

#### Scenario: `view.ptr` → I64
- **WHEN** `FieldGet dst, view, "ptr"` 执行
- **THEN** `dst` = `Value::I64(<原始 ptr 位>)`

#### Scenario: `view.len` → I64
- **WHEN** `FieldGet dst, view, "len"`
- **THEN** `dst` = `Value::I64(<长度>)`

#### Scenario: 未知字段 → Z0908
- **WHEN** `FieldGet dst, view, "lulz"`
- **THEN** VM 返回 `Err`，message 含 `"PinnedView"` + 字段名

---

### Requirement: marshal accepts PinnedView for native ptr/length args

#### Scenario: PinnedView 投 *const u8
- **WHEN** `marshal::value_to_z42(&pinned_view, &SigType::Ptr)`
- **THEN** 返回 `Z42Value { tag: NATIVEPTR, payload: ptr }`

#### Scenario: PinnedView 投 usize
- **WHEN** `marshal::value_to_z42(&pinned_view, &SigType::U64)`
- **THEN** 返回 `Z42Value { tag: I64, payload: len as u64 }`

#### Scenario: PinnedView 与不兼容类型组合 → Err
- **WHEN** `marshal::value_to_z42(&pinned_view, &SigType::F64)`
- **THEN** 返回 `Err`，含 `"PinnedView"`

---

### Requirement: 端到端 — String pin → CallNative → numz42-c strlen

#### Scenario: pin String → call c_strlen → 长度匹配
- **WHEN** 加载 `numz42-c` (含新增 strlen_with_hint 方法)；手工构造 IR：
  1. `r0 = ConstStr "hello world"`
  2. `r1 = PinPtr r0`
  3. `r2 = FieldGet r1, "ptr"`
  4. `r3 = FieldGet r1, "len"`
  5. `r4 = CallNative numz42::Counter::strlen_with_hint(r2, r3)`
  6. `UnpinPtr r1`
  7. `Ret r4`
- **THEN** 返回 `Value::I64(11)`

> **注意**：上面 step 5 调用形式是测试用，C4 不要求 numz42-c 必须有 strlen_with_hint method；如果 numz42-c 设计上不便添加，可简化为只验证 pin → field_get 的 ptr/len 与字符串内容一致（不实际跨 FFI）。

---

### Requirement: Z42_VALUE_TAG_PINNED_VIEW 常量冻结

#### Scenario: 常量值
- **WHEN** 检查 `z42_abi::Z42_VALUE_TAG_PINNED_VIEW`
- **THEN** 等于 `8`（紧接 C2 钉死的 0..7）

## IR Mapping

不新增 opcode；C1 已声明的 `PinPtr` (0x90) / `UnpinPtr` (0x91) 在 C4 接通真实语义。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / Codegen — 不涉及（z42 语法在 C5 引入）
- [x] VM interp — PinPtr/UnpinPtr 真实实现 + FieldGet PinnedView 分支
- [x] z42-abi — 新增 `Z42_VALUE_TAG_PINNED_VIEW`
- [ ] JIT — 不涉及（仍 bail!）

## Documentation Sync

- `docs/design/error-codes.md` Z0908 从占位 → 已启用（3 个抛出条件）
- `docs/design/interop.md` §6.3 / §10 更新
- `docs/design/ir.md` PinPtr/UnpinPtr 段补 runtime 语义
- `docs/design/vm-architecture.md` Value 变体表加 PinnedView
- `docs/roadmap.md` C4 → ✅
