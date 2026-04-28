# Proposal: Remove Dead Value::Map Variant

## Why

`Value::Map(Rc<RefCell<HashMap<String, Value>>>)` 是 z42 早期 L1 阶段
`Dictionary<K,V>` pseudo-class 的运行时后端。**2026-04-26 extern-audit-wave0** 把
`Std.Collections.Dictionary` 改造为纯 z42 脚本类（基于 `T[]` 实现），所有 `__dict_*`
builtin 一并删除（见 [`corelib/mod.rs:20-22`](../../../src/runtime/src/corelib/mod.rs)）。

至此 `Value::Map` 已无任何**创建路径**：

- 全代码库 grep 仅有 1 处构造点 —— 2026-04-29 add-magrgc-heap-interface 误加的
  `RcMagrGC::alloc_map()`（**该接口本身从未被任何 callsite 调用**）
- 5 处 match arm 是历史遗留的消费侧防御性代码（`ArrayGet` / `ArraySet` /
  `FieldGet` 在 interp + JIT 双端各 1 处，外加 `__len` builtin 1 处）
- `PartialEq` 实现 1 处兜底分支
- `value_to_str` 通过 `_ => format!("{:?}", other)` 兜底

按 `.claude/rules/workflow.md` "不为旧版本提供兼容（pre-1.0）" 原则：dead code
直接删除，不留空架。本 spec 把 `Value::Map` variant 与所有相关代码（接口、消费、兜底）
一并清理，让 `Value` enum 与 `MagrGC` trait 双双精简。

## What Changes

- 删除 `Value::Map` variant
- 删除 `MagrGC::alloc_map()` trait 方法 + `RcMagrGC` 的 impl + 相关测试
- 删除 5 处 Value::Map 消费 match arm（interp 3、jit 3、corelib/io 1）—— 等同 6 处
- 删除 `PartialEq for Value` 中的 Map 分支
- `value_to_str` 改为 exhaustive match（删除 `other =>` 兜底，借编译器强制覆盖所有 variant）
- 文档同步：`gc/README.md` + `vm-architecture.md` 移除 `alloc_map` 与 Map 相关描述

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/types.rs` | MODIFY | 删除 `Map` variant + `PartialEq` Map arm |
| `src/runtime/src/gc/heap.rs` | MODIFY | 删除 `alloc_map` trait method + 相关注释 |
| `src/runtime/src/gc/rc_heap.rs` | MODIFY | 删除 `alloc_map` impl |
| `src/runtime/src/gc/rc_heap_tests.rs` | MODIFY | 删除 `alloc_map_returns_empty_value_map` 测试，更新 `stats_allocations_monotonically_increases` |
| `src/runtime/src/gc/README.md` | MODIFY | 更新已知限制段（移除 alloc_map 占位说明）|
| `src/runtime/src/vm_context_tests.rs` | MODIFY | `two_contexts_heap_isolated` 改用第二次 `alloc_array` 代替 `alloc_map` |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 移除 `ArrayGet` / `ArraySet` / `FieldGet` 的 Value::Map 分支 |
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | 移除 `jit_array_get` / `jit_array_set` / `jit_field_get` 的 Value::Map 分支 |
| `src/runtime/src/corelib/io.rs` | MODIFY | 移除 `__len` 的 Value::Map 分支 |
| `src/runtime/src/corelib/convert.rs` | MODIFY | `value_to_str` 改为 exhaustive match，移除 `other =>` 兜底 |
| `docs/design/vm-architecture.md` | MODIFY | "GC 子系统" 段：删除 alloc_map 已知限制条 |

**只读引用**：

- `src/runtime/src/corelib/io.rs` 与其它 builtin（确认无其它 Map 用法）
- `src/compiler/` 全部（已确认 C# 侧的 Dictionary 都是 .NET BCL，与 z42 Map 无关）

## Out of Scope

- 后续 GC 接口扩展（Spec 2 `expand-magrgc-mmtk-interface`）
- corelib 内 `Rc::new(RefCell::new(...))` 直构（Phase 1.5 处理）

## Open Questions

无。删除范围由 grep 完整确定，无歧义。
