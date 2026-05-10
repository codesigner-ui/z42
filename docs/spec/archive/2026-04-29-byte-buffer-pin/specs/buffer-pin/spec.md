# Spec: Array<u8> pin (C10)

## ADDED Requirements

### Requirement: PinPtr accepts `Value::Array` of u8-range I64 elements

#### Scenario: 数组拷贝成 Box<[u8]>，view ptr 非空
- **WHEN** PinPtr 收到 `Value::Array(arr)` where arr = [I64(0x68), I64(0x69)] (= "hi" bytes)
- **THEN** dst = `Value::PinnedView { ptr: <非零>, len: 2, kind: ArrayU8 }`；ptr 地址处的字节内容 == [0x68, 0x69]

#### Scenario: 元素超出范围 → Z0908
- **WHEN** Array 含 `Value::I64(256)`
- **THEN** Err Z0908 含 "Array element ... not in 0..=255"，指出索引位置

#### Scenario: 元素非 I64 → Z0908
- **WHEN** Array 含 `Value::Bool(true)`
- **THEN** Err Z0908

#### Scenario: 空 Array → 长度 0 但 ptr 非空
- **WHEN** PinPtr 收到 `Array([])`
- **THEN** view len = 0；ptr 是合法地址（empty Box::as_ptr 返回 dangling-but-aligned）

---

### Requirement: UnpinPtr 释放 ArrayU8 buffer

#### Scenario: UnpinPtr 后 VmContext 副表无该 ptr
- **WHEN** PinPtr Array 后 vm.pinned_owned_buffers 含 ptr key；UnpinPtr 后副表 remove 该 key
- **THEN** Box<[u8]> drop，内存释放

#### Scenario: UnpinPtr Str view 副表不动
- **WHEN** PinPtr Str → UnpinPtr
- **THEN** vm.pinned_owned_buffers 全程为空（Str 路径无 owned buffer）

---

### Requirement: 端到端 — z42 byte[] → native 函数读字节

#### Scenario: pin Array<u8> + CallNative buflen → 长度正确
- **WHEN** 手工 IR：构造 `Array([0x68, 0x69, 0x21])` → PinPtr → FieldGet ptr/len → CallNative `numz42::Counter::buflen(ptr, len)` → assert == 3
- **THEN** native 端读到 ptr 处的 3 字节并返回 3

## IR Mapping

不新增 opcode；复用 PinPtr (0x90) / UnpinPtr (0x91)；运行时识别 Array source。

## Pipeline Steps

- [x] VM interp — PinPtr/UnpinPtr Array<u8> 分支
- [x] VmContext — pinned_owned_buffers 副表
- [ ] 其他 — 不涉及

## Documentation Sync

- error-codes.md Z0908 抛出条件加 (e) "Array element out of u8 range / non-I64"
- interop.md / roadmap.md 加 C10 行
