# Proposal: D-1a — corelib WeakRef builtins + Std.WeakHandle

## Why

z42 GC heap trait 已有 `make_weak` / `upgrade_weak` API ([heap.rs:164-167](src/runtime/src/gc/heap.rs#L164-L167))，但 corelib **未暴露 builtin**，所以 stdlib 无法用脚本实现 `WeakRef<T>` wrapper（D2b CompositeRef.Mode.Weak 占位长期不可达）。

本 spec **仅暴露底层 builtin + 不透明 handle 类** —— 用户 / stdlib 可以基于此手工写 weak 持有逻辑。完整 `Std.WeakRef<TD>` ISubscription wrapper 留 D-1b follow-up（依赖类型系统 + Closure.env 路径设计）。

## What Changes

- **VM**：
  - `metadata::NativeData` 加 `WeakRef(GcWeakRef)` variant —— 在 ScriptObject 内部携带 GC 弱引用句柄
  - `corelib::object::builtin_obj_make_weak(target)` —— 接 Object/Array 弱化，返回包装 NativeData::WeakRef 的 Object；非 Object/Array 返回 Null
  - `corelib::object::builtin_obj_upgrade_weak(handle)` —— 接 WeakHandle Object，upgrade 内部 GcWeakRef，返回原 Object 或 Null
- **stdlib**：
  - NEW `Std.WeakHandle` 不透明类（无字段，仅 native data）+ static `MakeWeak(object) → WeakHandle?` / `Upgrade(WeakHandle) → object?` 工厂方法 + 实例方法

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/types.rs` | MODIFY | `NativeData::WeakRef(WeakRef)` variant |
| `src/runtime/src/corelib/object.rs` | MODIFY | `builtin_obj_make_weak` + `builtin_obj_upgrade_weak` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | dispatch_table 注册 2 个 builtin |
| `src/runtime/src/corelib/tests.rs` | MODIFY | 单元测试 |
| `src/libraries/z42.core/src/WeakHandle.z42` | NEW | 不透明类 + static MakeWeak / Upgrade |
| `src/runtime/tests/golden/run/weak_ref_basic/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/weak_ref_basic/expected_output.txt` | NEW | 预期输出 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | 43 → 44 |

## Out of Scope

- `Std.WeakRef<TD>` ISubscription wrapper（→ D-1b follow-up）
- 接入 D2b CompositeRef.Mode.Weak（→ D-1b）
- Closure.env weak path（design line 191：handler.Target weak / handler.Method strong）→ D-1b 处理

## Open Questions

- [ ] WeakHandle 类是 z42 stdlib 普通 class 还是需要 compiler 特判（如 PinnedView 是 Z42PrimType）？倾向：普通 class with NativeData backing，VM 端走标准 alloc_object 路径
