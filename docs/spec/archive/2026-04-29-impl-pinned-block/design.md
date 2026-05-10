# Design: PinPtr/UnpinPtr runtime + PinnedView Value (C4)

## Architecture

```
                   ┌─────────────────────────────────────────────────┐
                   │ z42 source (C5 will add `pinned p = s { ... }`) │
                   │ → emits PinPtr / UnpinPtr around block          │
                   └────────────────────┬────────────────────────────┘
                                        │ (C5 work)
                                        ▼
┌──────────────────────────────────────────────────────────────────┐
│ IR: PinPtr dst src   /   UnpinPtr pinned   /   FieldGet view "ptr"│
└────────────────────────────────┬─────────────────────────────────┘
                                 │ C4 wires runtime ↓
┌──────────────────────────────────────────────────────────────────┐
│ Interp `exec_instr.rs`                                           │
│   PinPtr  → dispatch on Value variant                            │
│            Str → ptr+len from String bytes                       │
│            Array<u8> → ptr+len from Vec<u8>                      │
│            other → bail Z0908                                    │
│   UnpinPtr → require PinnedView; no-op (RC); else bail Z0908     │
│   FieldGet on PinnedView → "ptr" / "len" → Value::I64            │
├──────────────────────────────────────────────────────────────────┤
│ Value::PinnedView { ptr: u64, len: u64, kind: PinSourceKind }    │
│ PinSourceKind { Str, ArrayU8 }                                   │
├──────────────────────────────────────────────────────────────────┤
│ marshal::value_to_z42 — accepts PinnedView                       │
│   target = SigType::Ptr → payload = view.ptr                     │
│   target = SigType::CStr → payload = view.ptr                    │
│   target = SigType::U64 → payload = view.len                     │
└──────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: `Value::PinnedView` 形状

```rust
#[derive(Debug, Clone)]
pub enum Value {
    // ... 现有变体 ...
    PinnedView { ptr: u64, len: u64, kind: PinSourceKind },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PinSourceKind {
    Str,
    ArrayU8,
}
```

`ptr` / `len` 用 `u64`（不用 raw pointer）：
- `Value` clone / `PartialEq` 派生简单
- payload 直接走 `Z42Value.payload` 路径
- `*const u8` semantics 由消费方（marshal）解释

`PinSourceKind` 仅 C4 内部用于 unpin 校验 / debug print；marshal 不区分（都当 `*const u8` 看）。

### Decision 2: `PinPtr` runtime 实现

```rust
Instruction::PinPtr { dst, src } => {
    let v = frame.get(*src)?;
    let view = match v {
        Value::Str(s) => Value::PinnedView {
            ptr: s.as_ptr() as u64,
            len: s.len() as u64,
            kind: PinSourceKind::Str,
        },
        Value::Array(arr) => {
            let buf = arr.borrow_buffer();  // 假设有这个 helper；见 Decision 4
            // 仅支持 Array<u8>;其他元素类型 bail
            // 这里需要 Array<Value> 中所有元素是 Value::I64 范围 0..=255?
            // 实际 z42 Array<u8> 在 VM 内是 Array<Value> with each Value being I64 of 0..=255
            // C4 简化:  反推不便，暂支持 String 与 Array<Value> 元素类型由调用方保证
            // 通过自定义专用 buffer view 后端实现
            todo!("Array<u8> pin path: 见 Decision 4")
        }
        other => anyhow::bail!("Z0908: PinPtr source must be String or Array<u8>, got {:?}", other),
    };
    frame.set(*dst, view);
}
```

### Decision 3: `Array<u8>` pin 处理（限制与简化）

z42 当前 `Array` 是 `GcRef<Vec<Value>>`，每个元素是 `Value::I64`。这意味着 native 拿到的 `*const u8` 不能直接是 z42 数组的 backing buffer——backing 实际上是 8-byte `Value` slots。

**两个选择**：
- A: 引入 `Value::Buffer<u8>`（紧凑 byte buffer）专门给 native interop；TypeChecker / stdlib 把 `byte[]` 物化成 Buffer
- B: PinPtr 拒绝 `Array<Value>`，只支持 `Value::Str`（`String` 的 backing UTF-8 buffer 直接借出）

C4 选 **B**：仅支持 `Value::Str`；`Value::Array` PinPtr 报 `Z0908: Array<u8> pinning lands in a follow-up spec (deferred until Buffer/byte type is introduced)`。

`PinSourceKind::ArrayU8` 仍登记，便于将来扩展，C4 实际不会构造该 kind。

### Decision 4: `UnpinPtr` runtime

```rust
Instruction::UnpinPtr { pinned } => {
    let v = frame.get(*pinned)?;
    match v {
        Value::PinnedView { .. } => {
            // RC backend: no-op. Future moving GC will remove from pin set.
        }
        other => anyhow::bail!(
            "Z0908: UnpinPtr expects PinnedView, got {:?} (compiler-emitted UnpinPtr should always pair with PinPtr)",
            other
        ),
    }
}
```

dst 没用（UnpinPtr 无返回值）。z42 编译器（C5）在所有出口路径插 UnpinPtr 即可保证配对。

### Decision 5: PinnedView `.ptr` / `.len` 字段访问

走现有 `Instruction::FieldGet` 路径。在 `exec_instr.rs::FieldGet` 加 PinnedView 分支：

```rust
Value::PinnedView { ptr, len, .. } => {
    let v = match field_name.as_str() {
        "ptr" => Value::I64(*ptr as i64),
        "len" => Value::I64(*len as i64),
        other => anyhow::bail!("PinnedView has no field {other:?} (only `ptr` / `len`)"),
    };
    frame.set(*dst, v);
}
```

C5 source generator emit 的 `pinned p = s { use(p.ptr, p.len) }` 会编译成 FieldGet 指令读 `ptr` / `len`，与现有 `string.Length` 类型字段访问对称。

### Decision 6: `marshal::value_to_z42` 接 PinnedView

```rust
match (v, target) {
    // ... 现有分支 ...
    (Value::PinnedView { ptr, .. }, SigType::Ptr | SigType::SelfRef | SigType::CStr) => {
        Ok(dispatch::z42_native_ptr(*ptr as usize as *mut c_void))
    }
    (Value::PinnedView { len, .. }, SigType::U64 | SigType::I64 | SigType::U32 | SigType::I32) => {
        // 长度入参（usize 等）
        Ok(dispatch::z42_i64(*len as i64))
    }
    // 其他 PinnedView × 非匹配类型 → 报错
}
```

**注意**：marshal 选择 `ptr` vs `len` 的依据是**目标 SigType**——如果两个连续参数分别是 `*const u8` 和 `usize`，IR 必须显式把同一 PinnedView 作为两次参数传过去（一次取 ptr、一次取 len）。这与 C5 source generator 计划一致：`c_write(p.ptr, p.len)` 编译为两个独立的 `FieldGet` 然后入参。

> 实际上 marshal 看到的 PinnedView 已经被 `FieldGet` 拆开了——不会有"原始 PinnedView 入 native 参数"的情况。Decision 6 主要是 defensive：万一 IR 直接传 PinnedView，marshal 给一个合理 fallback。

### Decision 7: 错误码 Z0908 抛出条件

| 抛出点 | 条件 |
|--------|------|
| `Instruction::PinPtr` | source 不是 `Value::Str`（C4 唯一支持的 pinnable） |
| `Instruction::UnpinPtr` | argument 不是 `Value::PinnedView` |
| `Instruction::FieldGet` on PinnedView | field 名既不是 `ptr` 也不是 `len` |

错误信息含 `"Z0908"` 前缀 + 具体上下文。

### Decision 8: numz42-c PoC 增加 `c_strlen` 验证

`tests/data/numz42-c/numz42.c` 加：

```c
#include <string.h>

static u64 counter_strlen(const char* s, uint64_t hint_len) {
    (void)hint_len;
    return strlen(s);
}

// 加进 method 表（Counter 类下，签名 `(*const u8, u64) -> u64`）
{ "strlen_with_hint", "(*const u8, u64) -> u64", (void*)counter_strlen, 0, 0 },
```

> Counter 类型加非真正 instance method 的 `strlen` 看起来怪 ——更干净的做法是引入新类型 `Pinned` 或 `StringOps`。但 C4 测试目的就是"验证 PinnedView ptr 能进 native"，把这函数挂在 Counter 是最少改动方案；C5 source generator 阶段会重整 numz42-c PoC。

## Implementation Notes

### Value::PinnedView 的 GC 语义

`PinnedView` 不持有 source 的 GcRef。理论上如果 source 在 pin 期间被释放，`ptr` 变悬挂指针。但：
1. 编译器（C5）保证 pin 期间 source 局部变量不释放
2. RC backend 单线程，pin block 内不会进入 GC sweep
3. PinnedView 本身在 frame 中作为 Value 存在，不参与 cycle collection（无 GcRef 字段）

如果 future 引入 mark-sweep / 分代 GC，需要在 `external_root_scanner` 把 PinnedView 视作 root 同时 mark 其 source —— C4 注释提醒，但不实现。

### C5 syntax 接口预留

C5 用户代码：
```z42
pinned p = s {
    c_write(p.ptr, p.len);
}
```

应编译为：
```
PinPtr <view>, <s>
%ptr = FieldGet <view>, "ptr"
%len = FieldGet <view>, "len"
CallNative ... <ptr>, <len>
UnpinPtr <view>
```

C4 这条路径**已经全部就绪**——C5 只需做 syntax → IR codegen 一层。

### exec_instr.rs FieldGet 现有结构

需要查看现 FieldGet 实现（处理 string.Length 等内置字段）以决定 PinnedView 分支放置位置。预期是在 match 上类似 `Value::Str` 的 builtin 字段段。

## Testing Strategy

| 测试 | 位置 | 验证 |
|------|------|------|
| Value::PinnedView clone / 等价 | inline `types_tests.rs` | derive 行为 |
| PinPtr Str → PinnedView 正确 | `tests/native_pin_e2e.rs` | ptr 非 0、len = String.bytes().len() |
| PinPtr Array → bail Z0908 | 同 | C4 仅支持 Str |
| UnpinPtr PinnedView → ok | 同 | no-op |
| UnpinPtr 非 PinnedView → bail Z0908 | `tests/native_opcode_trap.rs` | 防御性 |
| FieldGet view.ptr / view.len | e2e | 返回 Value::I64 |
| FieldGet view.unknown_field → bail | e2e | message 含 ptr / len |
| Marshal PinnedView 入 *const u8 | `marshal_tests.rs` | ptr 投影正确 |
| numz42-c c_strlen via PinnedView | e2e | 端到端：String → pin → CallNative strlen → 期望长度 |
| 全绿 | dotnet test + ./scripts/test-vm.sh | 不回归 |

## Risk & Rollback

- **风险 1**：Value::PinnedView 引入破坏现有 match 分支（exec_instr / dispatch / serde）
  - 缓解：grep 所有 `match.*Value` 找到完整列表；每个加 PinnedView 分支或合理 catch-all
- **风险 2**：String 内部表示改变（如未来 Cow/Rc）导致 `as_ptr()` 不稳定
  - 缓解：z42 当前 `Value::Str(String)`，String::as_ptr 保证稳定；future 改 `Rc<String>` 时 PinPtr 需取内部 String 的 ptr
- **回滚**：把 PinPtr/UnpinPtr 分支改回 `bail!` 即可恢复 C2 行为；Value::PinnedView 与 marshal 改动可独立 git revert
