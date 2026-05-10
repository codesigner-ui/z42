# Proposal: Array<u8> pin support (C10)

## Why

C4 PinPtr 仅支持 `Value::Str`；`Array<u8>`（z42 `byte[]`）目前报 Z0908 拒绝。但**字节缓冲是 native 接口最常见的输入形式**——文件 IO、密码学、网络协议都把 raw bytes 传给 native 函数。

C10 让 PinPtr 支持 `Value::Array` 中元素全为 `Value::I64` (0..=255) 的情况。pin 时把数组扫成 `Box<[u8]>`，leak 数据指针并交给 VmContext 副表保活；UnpinPtr 时副表回收。

## What Changes

- **`VmContext`** 加 `pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>` 副表
- **`PinPtr` runtime**：识别 `Value::Array` source；扫元素验证 0..=255；构造 `Box<[u8]>`；leak ptr → 副表
- **`UnpinPtr` runtime**：`PinSourceKind::ArrayU8` 时 + ptr in 副表 → 取走 Box drop
- **错误**：元素超 255 / 非 I64 → Z0908 含越界元素位置
- **测试**：单元 + e2e（pin Array → strlen-style call）

## Scope

| 文件 | 变更 |
|------|------|
| `src/runtime/src/vm_context.rs` | 加 `pinned_owned_buffers` 字段 + `pin_owned_buffer(Box<[u8]>) -> u64` / `release_owned_buffer(ptr)` 方法 |
| `src/runtime/src/interp/exec_instr.rs` | PinPtr Array 分支；UnpinPtr ArrayU8 释放 |
| `src/runtime/tests/native_pin_e2e.rs` | 加 Array pin 单元测试 |
| `src/runtime/tests/native_interop_e2e.rs` | 加 e2e：z42 byte[] → CallNative 测长度 |
| `src/runtime/tests/data/numz42-c/numz42.c` | 加 `counter_buflen(*const u8, u64) -> u64`（接显式 len） |
| `docs/design/error-codes.md` | Z0908 抛出条件加 "Array element out of range" |
| `docs/design/interop.md` / `docs/roadmap.md` | C10 → ✅ |

## Out of Scope

- 任意元素类型 Array（`Array<i32>` 等）—— 需要 SigType 携带元素类型
- 写入 native → z42 数组（`*mut u8` output buffer）—— 异步 ABI，独立 spec
- z42 字节字面量 `b"hello"` —— 语法，独立 spec
- 跨多次 PinPtr 共享同一 buffer —— RC backend 不需要

## Open Questions

- [ ] **Q1**：Array<u8> 内 Value::I64 范围是 0..=255 还是 i8 范围（-128..=127）？
  - 倾向：**unsigned 0..=255**，与 C `uint8_t` / Rust `u8` 一致
- [ ] **Q2**：源 Array 在 pinned 期间被修改（追加 / set）的语义？
  - 倾向：pinned view 是 **快照**——在 PinPtr 时 copy 出 Box<[u8]>。源后续修改不影响 view。简单 & 安全。代价：每次 pin 一次拷贝
