# Spec: Str → CStr marshal (C8)

## ADDED Requirements

### Requirement: marshal arena pattern

#### Scenario: Arena owns CStrings during dispatch
- **WHEN** `marshal::value_to_z42(Value::Str("hi".into()), SigType::CStr, &mut arena)`
- **THEN** Returns `Z42Value` with `tag = NATIVEPTR` and `payload != 0`; arena now holds 1 CString

#### Scenario: Arena drops free CStrings
- **WHEN** Arena goes out of scope after a dispatch
- **THEN** No leaks (verified via no `#[deny(unsafe_code)]` issues + existing tests)

---

### Requirement: Str → CStr marshal

#### Scenario: Empty string
- **WHEN** marshal `Value::Str("")` to `SigType::CStr`
- **THEN** Z42Value with non-null ptr (CString allocates 1 byte for NUL); `*ptr == 0`

#### Scenario: ASCII string
- **WHEN** marshal `Value::Str("hello")` to `SigType::CStr`
- **THEN** Z42Value ptr addresses 6 bytes ('h','e','l','l','o','\0')

#### Scenario: UTF-8 multi-byte string
- **WHEN** marshal `Value::Str("héllo")` to `SigType::CStr`  
- **THEN** Z42Value ptr addresses the UTF-8 bytes + NUL; native consumer is responsible for UTF-8 awareness

#### Scenario: String with interior NUL → Z0908
- **WHEN** marshal `Value::Str("a\0b")` to `SigType::CStr`
- **THEN** Returns `Err` containing "Z0908" + "interior NUL"

---

### Requirement: Str → Ptr (unspecified element) also accepted

#### Scenario: Defensive raw-ptr path
- **WHEN** marshal `Value::Str("hello")` to `SigType::Ptr`
- **THEN** Same behaviour as `SigType::CStr` (Z42Value with NATIVEPTR tag pointing at a NUL-terminated buffer)

---

### Requirement: end-to-end Z42 string → native strlen

#### Scenario: Hand-crafted IR with CallNative strlen returning 5
- **WHEN** Module contains:
  1. `ConstStr r0 "hello"`
  2. `CallNative dst=r1 numz42::Counter::strlen [r0]`
  3. `Ret r1`
   And numz42-c's `counter_strlen` is registered as a static method with signature `(*const u8) -> i64`
- **THEN** VM run returns `Value::I64(5)`

## IR Mapping

不新增 IR opcode；复用 `CallNativeInstr` (0x53)。dispatch.rs `parse_signature` 已支持 `*const u8` 解析为 `SigType::Ptr`；marshal 把 z42 string 投影上去。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不涉及
- [x] VM marshal — 加 (Value::Str, SigType::CStr/Ptr) 分支
- [x] VM CallNative dispatch — 引入 Arena 作用域

## Documentation Sync

- `docs/design/interop.md` §6 marshal 表加 (Str, CStr) 行
- `docs/design/error-codes.md` Z0908 加 "interior NUL in marshal" 抛出条件
- `docs/roadmap.md` C8 → ✅
