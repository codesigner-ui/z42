# Design: Array<u8> pin support (C10)

## Architecture

```
PinPtr Value::Array(arr) →
    1. validate: every element is Value::I64 in 0..=255
    2. let bytes: Box<[u8]> = arr.iter().map(|v| v as u8).collect();
    3. let ptr = bytes.as_ptr() as u64;
    4. ctx.pin_owned_buffer(ptr, bytes);   // moves Box into HashMap<ptr, Box>
    5. PinnedView { ptr, len, kind: ArrayU8 }

UnpinPtr Value::PinnedView { kind: ArrayU8, ptr, .. } →
    ctx.release_owned_buffer(ptr);   // map.remove(ptr) drops the Box

UnpinPtr Value::PinnedView { kind: Str, .. } →
    no-op (str ptr is borrowed from source String, no ownership)
```

## Decisions

### Decision 1: Snapshot semantics

PinPtr Array 时**复制一次**到 `Box<[u8]>`。Pros:
- 简单：源 Array 后续修改不影响 view
- 安全：源 Array 释放（GC）时 view 仍有效
- RC backend 单线程下无竞争问题

Cons:
- 每 pin 一次拷贝（O(N)）

Alternative：直接借出 Vec<Value> 的 backing 内存——但 Vec<Value> 元素是 24-byte Value slots，不是连续 u8 字节。**不可行**——必须拷贝。

### Decision 2: Box<[u8]> ownership 副表

`VmContext.pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>` 以 ptr 为 key 持有 Box，确保数据在 pin 期间稳定。UnpinPtr 时 map.remove(ptr) drops Box。

风险：副表内 ptr key 与 PinnedView.ptr 必须保持一致。`Box::as_ptr()` 在 Box 移动期间稳定（只要 Box 不重分配），HashMap 存储 Box 不重分配数据。✓

### Decision 3: 元素验证

PinPtr 在拷贝前扫描所有元素：
- 全部是 `Value::I64(n)` 且 0 ≤ n ≤ 255 → ok
- 否则 Z0908 含 first-bad-index + 元素描述

性能：与拷贝合并到一遍 scan。

### Decision 4: 不引入新 Value variant

不新增 `Value::Buffer<u8>`：
- 引入新 variant 改动面大（GC trace、dispatch 等）
- 当前 z42 `byte[]` = `Array<i64>`（每元素低 8 位 = byte 值）—— 复用现有 Array 表示
- C10 仅在 marshal/pin 边界做"按字节解释"

副作用：z42 用户代码看 `byte[]` 仍是 `i64[]`，需要约定 0..=255 范围。这与当前 stdlib 用法一致。

## Implementation

### VmContext additions

```rust
// vm_context.rs
pub struct VmContext {
    // ... existing
    pub(crate) pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>,
}

impl VmContext {
    pub fn new() -> Self {
        Self {
            // ... existing
            pinned_owned_buffers: Rc::new(RefCell::new(HashMap::new())),
        }
    }

    /// Spec C10 — register an owned byte buffer whose raw pointer was
    /// handed out via `PinPtr` for `Array<u8>`. The buffer remains alive
    /// until `release_owned_buffer(ptr)` (called from `UnpinPtr`).
    pub(crate) fn pin_owned_buffer(&self, buf: Box<[u8]>) -> u64 {
        let ptr = buf.as_ptr() as u64;
        self.pinned_owned_buffers.borrow_mut().insert(ptr, buf);
        ptr
    }

    pub(crate) fn release_owned_buffer(&self, ptr: u64) {
        // No-op if the buffer wasn't ours (e.g. Str case where the pointer
        // came from the source `String`); harmless under HashMap.remove.
        let _ = self.pinned_owned_buffers.borrow_mut().remove(&ptr);
    }
}
```

### PinPtr runtime extension

```rust
Instruction::PinPtr { dst, src } => {
    let view = match frame.get(*src)? {
        Value::Str(s) => Value::PinnedView {
            ptr: s.as_ptr() as u64,
            len: s.len() as u64,
            kind: PinSourceKind::Str,
        },
        Value::Array(arr) => {
            let arr = arr.borrow();
            let mut bytes = Vec::with_capacity(arr.len());
            for (i, v) in arr.iter().enumerate() {
                match v {
                    Value::I64(n) if (0..=255).contains(n) => bytes.push(*n as u8),
                    other => bail!(
                        "Z0908: PinPtr Array element {i} not in 0..=255: {other:?}"
                    ),
                }
            }
            let len = bytes.len() as u64;
            let buf: Box<[u8]> = bytes.into_boxed_slice();
            let ptr = ctx.pin_owned_buffer(buf);
            Value::PinnedView { ptr, len, kind: PinSourceKind::ArrayU8 }
        }
        other => bail!("Z0908: PinPtr source must be String or Array<u8>, got {other:?}"),
    };
    frame.set(*dst, view);
}
```

### UnpinPtr runtime extension

```rust
Instruction::UnpinPtr { pinned } => {
    match frame.get(*pinned)? {
        Value::PinnedView { ptr, kind: PinSourceKind::ArrayU8, .. } => {
            ctx.release_owned_buffer(*ptr);
        }
        Value::PinnedView { kind: PinSourceKind::Str, .. } => {
            // borrowed from source String — nothing to release
        }
        other => bail!("Z0908: UnpinPtr expects PinnedView, got {other:?}"),
    }
}
```

## Testing

| Test | Verifies |
|------|----------|
| `pin_array_u8_snapshots_bytes` | Array<u8> pin → ptr non-zero, len matches; ptr addresses valid bytes |
| `pin_array_u8_element_out_of_range_z0908` | element 256 (or -1) → Z0908 with index |
| `pin_array_u8_non_i64_z0908` | element Value::Bool → Z0908 |
| `unpin_array_u8_releases_buffer` | UnpinPtr drops the Box (vm_context map empty after) |
| `pin_array_u8_then_call_buflen` (e2e) | pin → CallNative numz42::Counter::buflen(ptr, len) → returns len |
| `pin_str_after_c10_no_regression` | Str path still works (PinPtr returns view, UnpinPtr no-op) |
