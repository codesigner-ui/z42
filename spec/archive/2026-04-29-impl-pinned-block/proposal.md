# Proposal: Pin/Unpin runtime + PinnedView Value (C4)

## Why

C1 把 `PinPtr` (0x90) / `UnpinPtr` (0x91) 两个 IR opcode 钉死格式；C2 把 Tier 1 C ABI 接通；C3 把 Tier 2 Rust macros 接通。但**用户代码仍没法把 String / Array 借给 native 函数**——marshal 拒绝 `Value::Str` / `Value::Array<u8>`，这正是 PinPtr 设计要解决的。

C4 把 pin 协议在 VM runtime 完整接通：
- 新增 `Value::PinnedView { ptr, len, kind }` variant
- `PinPtr` opcode runtime 从 `Value::Str` / `Value::Array<u8>` 提取底层 ptr+len，构造 PinnedView
- `UnpinPtr` opcode runtime（RC backend 下 no-op；接口完整保留给 future moving GC）
- `marshal::value_to_z42` 接受 PinnedView 投影出 `*const u8` / `usize` 给 CallNative
- 错误码 Z0908 实现（运行时拒绝非 pinnable source / 释放非 pinned 句柄）

**C4 不引入 z42 用户代码层的 `pinned` 关键字 / 语法**——那条路径与 `[Native]` / `import T from "lib"` 同属 user-facing FFI 层，统一留给 C5 source generator spec 一次性引入（避免 lexer/parser/typecheck 的重复改动）。本 spec 测试通过手工构造 zbc + 集成测试验证。

## What Changes

- **Rust VM**：
  - `Value` enum 新增 `PinnedView { ptr: u64, len: u64, kind: PinSourceKind }` variant + `PinSourceKind { Str, ArrayU8 }` enum
  - `PinPtr` runtime：source = `Value::Str` → 抽 UTF-8 ptr+len；source = `Value::Array<u8>` → 抽元素 ptr+len；其他类型 → bail Z0908
  - `UnpinPtr` runtime：dst 为 `Value::PinnedView` 时 no-op；非 PinnedView → bail Z0908（防御性）
  - `marshal::value_to_z42` 处理 `Value::PinnedView`：
    - 目标 `SigType::Ptr` / `SigType::CStr` → 取 `payload = ptr`
    - 目标 `SigType::U64`/`I64` → 取 `payload = len`
    - 提供两个伴生入口：`pinned_ptr_arg(view)` / `pinned_len_arg(view)`，调用方按需选择
- **错误码 Z0908 启用**：
  - `Z0908_NotPinnable` (VM)：PinPtr 收到 `Value::Object` / `Value::I64` 等非 pinnable 类型
  - `Z0908_UnpinNonPinned` (VM)：UnpinPtr 收到非 PinnedView
- **z42-abi 公开 `Z42_VALUE_TAG_PINNED_VIEW = 8`**（保留 tag 8 给 C4，与 C2 钉死的 0..7 顺延）
- **测试**：
  - `tests/native_opcode_trap.rs` 删 PinPtr/UnpinPtr 的 trap 测试；加 PinPtr 正常构造 PinnedView 的测试
  - `tests/native_pin_e2e.rs` NEW：手工 IR 构造 `PinPtr s -> view`，调用 `field_get view.ptr`、`view.len`，断言值
  - 现有 `dispatch::call` + marshal 路径下游验证 PinnedView ptr 能作为 native ptr 参数（带最简 C 函数 `c_strlen` 入 numz42-c）
- **文档同步**：
  - `docs/design/error-codes.md` Z0908 从占位 → 已启用
  - `docs/design/interop.md` §6.3 描述 PinnedView Value 形状 + Z42_VALUE_TAG_PINNED_VIEW = 8
  - `docs/design/ir.md` PinPtr/UnpinPtr 段补 runtime 语义
  - `docs/roadmap.md` C4 → ✅
  - `docs/design/vm-architecture.md` Value 变体表补一行

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/crates/z42-abi/src/lib.rs` | MODIFY | 新增 `pub const Z42_VALUE_TAG_PINNED_VIEW: u32 = 8` |
| `src/runtime/crates/z42-abi/tests/abi_layout_tests.rs` | MODIFY | 加 tag 值断言 |
| `src/runtime/src/metadata/types.rs` | MODIFY | `Value` 加 `PinnedView { ptr: u64, len: u64, kind: PinSourceKind }` + `PinSourceKind` enum |
| `src/runtime/src/metadata/types_tests.rs` (or inline) | MODIFY | Value::PinnedView clone / PartialEq 测试 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | PinPtr/UnpinPtr trap → 真实实现；FieldGet 加 PinnedView.ptr/.len 分支 |
| `src/runtime/src/jit/translate.rs` | MODIFY | match 加 PinnedView 排除 / max_reg 已含；保持 JIT bail |
| `src/runtime/src/native/marshal.rs` | MODIFY | `value_to_z42` 接受 PinnedView 投 ptr/len；`z42_to_value` 不需要支持（只输入侧） |
| `src/runtime/src/native/marshal_tests.rs` | MODIFY | 加 PinnedView marshal 测试 |
| `src/runtime/src/native/dispatch.rs` | MODIFY | `Z42_VALUE_TAG_PINNED_VIEW` re-export 给 marshal 使用（如果不直接走 z42-abi） |
| `src/runtime/tests/native_opcode_trap.rs` | MODIFY | PinPtr trap 测试改为正常 view 构造；UnpinPtr trap 测试改为非 pinned source → Z0908 |
| `src/runtime/tests/native_pin_e2e.rs` | NEW | 端到端：手工 IR 构造 pin → field access → unpin |
| `src/runtime/tests/data/numz42-c/numz42.c` | MODIFY | 加一个 `c_strlen(*const u8, usize) -> u64` 函数验证 pinned ptr 能进 native |
| `src/runtime/tests/data/numz42-c/numz42.c` 注册 | 包含在上一行 | strlen 加进 method 表 |
| `docs/design/interop.md` | MODIFY | §6.3 补 PinnedView 字段 + tag |
| `docs/design/error-codes.md` | MODIFY | Z0908 从占位 → 已启用 |
| `docs/design/ir.md` | MODIFY | PinPtr/UnpinPtr 段更新运行时语义 |
| `docs/design/vm-architecture.md` | MODIFY | Value 变体表加一行 |
| `docs/roadmap.md` | MODIFY | C4 → ✅ |

**只读引用**：
- `src/runtime/src/native/registry.rs` — 理解 method 注册路径
- `src/runtime/src/interp/exec_instr.rs::CallNative` — 理解 marshal 与 dispatch 协作
- `src/runtime/tests/native_interop_e2e.rs` — 测试模式参考

## Out of Scope

- **z42 用户代码 `pinned` 块语法（lexer / parser / typecheck / source-side IR codegen）**：与 `[Native]` / `import T from "lib"` / source generator 一起在 **C5 (`impl-source-generator`)** 引入，避免 C# 编译器多次 churn
- **Moving GC 实际 pin set 注册**：RC backend 下 pin 是无操作；接口保留供 future
- **JIT 后端 emit PinPtr/UnpinPtr**：L3.M16
- **元素类型非 `u8` 的 Array pinning**（如 `Array<i32>`）：需要 ABI 协商元素步长；C4 仅支持 `Array<u8>` 与 `String`，足够 PoC

## Open Questions

- [ ] **Q1**：`Value::PinnedView` 的 `ptr` 类型是 `u64` 还是 `*const u8`？
  - 倾向：**`u64`**。`*const T` 让 `Value` `!Send`，但 `Value` 已包含 `GcRef`（也 `!Send`），区别不大；`u64` 让 `PartialEq` derive 简单。两者等价，选 `u64` 更便于 marshal 直接走 payload
- [ ] **Q2**：UnpinPtr 收到非 PinnedView 是 hard error 还是 silent no-op？
  - 倾向：**hard error (Z0908)**。z42 编译器只在配对位置 emit UnpinPtr；运行时见到非 PinnedView 一定是 IR 损坏 / 第三方修改字节码 → 让用户立刻看到错误而不是被吞
- [ ] **Q3**：`PinPtr` 收到 RC=0 的 String / Array（理论不可能，但保险）行为？
  - 倾向：编译器路径保证 source 仍持有引用（PinnedView 持有的 ptr 在 source 仍可达期间稳定）；C4 不加额外运行时检查
