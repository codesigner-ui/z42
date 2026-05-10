# Tasks: split-rc-heap-tests

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.4（Rust 部分；C# 已通过 split-typechecker-tests 完成）

## 验证报告

### 编译状态
- ✅ `cargo build --manifest-path src/runtime/Cargo.toml`: 0 warning
- ✅ `dotnet build src/compiler/z42.slnx -c Debug --no-incremental`: 0 Error / 0 Warning（清空 obj/ 缓存规避 pre-existing MSB3492，与前两次 spec 同款）

### 测试结果
- ✅ `cargo test`: 全绿
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**
- ✅ rc_heap 测试 #[test] 总数：原 83 = 拆后 83（精确一致）

### LOC 目标
- 老 rc_heap_tests.rs: 1229 LOC → 删除
- 10 个新文件全部 ≤ 300 软限：
  - cycle_collection.rs: 228
  - roots.rs: 212
  - finalization.rs: 155
  - weak_refs.rs: 155
  - events.rs: 131
  - collection.rs: 93
  - oom.rs: 91
  - config_stats.rs: 61
  - object_model.rs: 55
  - allocation.rs: 45
  - mod.rs: 33（共享 helper + 10 个 mod 声明）

### 实施备注
- rc_heap.rs 的 `#[path = "rc_heap_tests.rs"]` 改为 `#[path = "rc_heap_tests/mod.rs"]`（rc_heap.rs 不是 mod.rs，子模块默认查找规则不直接命中子目录，仍需显式 path 属性）
- 共享 helper `dummy_type_desc` 改为 `pub(super) fn` 让子模块通过 `use super::*` 访问

### 结论：✅ 全绿，可归档

**变更说明**: 把 `src/runtime/src/gc/rc_heap_tests.rs` (1229 LOC, 83 个 `#[test]`) 按 22 个 `// ── XXX` 分类头合并拆分到 `rc_heap_tests/` 子目录下 9 个 topic 文件，每个 ≤ 250 LOC。

**原因**: 单文件 1229 LOC 超 600 软限。GC 测试天然分组（allocation / roots / weak refs / cycle collection / finalization 等），按主题拆后定位明确。**纯文件搬运 + 共享 helper 提升到 mod.rs，零代码变化**。

**文档影响**:
- `docs/review.md` — 路线图 §VM 线 `split-rc-heap-tests` 状态 📋 → 🟢；§1.4 整体收口
- `src/runtime/src/gc/README.md` — 同步核心文件表（如有）

---

## Scope（允许改动的文件）

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/runtime/src/gc/rc_heap.rs` | 第 943-945 行 `#[path = "rc_heap_tests.rs"] mod rc_heap_tests;` → `mod rc_heap_tests;`（让 Rust 默认查 `rc_heap_tests/mod.rs`） |
| `docs/review.md` | 路线图状态更新 |

### NEW (1 mod.rs + 9 topic files)

| 文件 | 涵盖类别（原 // ── 行号） | 估计 LOC | `#[test]` 数 |
|---|---|---|---|
| `rc_heap_tests/mod.rs` | use 语句 + `dummy_type_desc` helper + `mod ...;` 声明 | ~30 | 0 |
| `rc_heap_tests/allocation.rs` | §1 (24-66) | 43 | 4 |
| `rc_heap_tests/roots.rs` | §2 + Interp stack scanning Phase 3f + External root scanner Phase 3d.1 | 210 | 13 |
| `rc_heap_tests/object_model.rs` | §4 (130-182) | 53 | 5 |
| `rc_heap_tests/collection.rs` | §5 (183-241) + Auto-collect (477-508) | 91 | 8 |
| `rc_heap_tests/cycle_collection.rs` | Cycle collection Phase 3c (856-1081) | 226 | ~12 |
| `rc_heap_tests/finalization.rs` | §7-1 (3e) + §7 (3d) + §7-existing (3a baseline) | 153 | ~10 |
| `rc_heap_tests/weak_refs.rs` | §8 (535-579) + §8.5 Handle table (1122-1229) | 153 | ~9 |
| `rc_heap_tests/events.rs` | §9 Event observers + §10 Profiler | 129 | ~6 |
| `rc_heap_tests/oom.rs` | §6-1 Strict OOM Phase 3-OOM (242-330) | 89 | ~5 |
| `rc_heap_tests/config_stats.rs` | §6 Heap config (331-349) + §11 Stats (1082-1121) | 59 | ~3 |

### DELETE

| 文件 | 说明 |
|---|---|
| `src/runtime/src/gc/rc_heap_tests.rs` | 内容全部迁入 `rc_heap_tests/` 子目录 |

**只读引用**:
- `src/runtime/src/gc/rc_heap.rs` — 测试目标，子模块通过 `use super::super::*` 或 `use crate::gc::*` 访问

---

## 设计要点

### `#[path]` 移除的合理性

当前 rc_heap.rs:943-945 用了 `#[path = "rc_heap_tests.rs"]` 显式声明。这在历史上可能是为了避免与某个同名模块冲突，但实际只需 `mod rc_heap_tests;` 即可——Rust 默认按名称查找 `rc_heap_tests.rs` 或 `rc_heap_tests/mod.rs`，前者为单文件、后者为子目录。删 `#[path]` 后子目录方案自动生效。

### mod.rs 共享内容

```rust
//! `RcMagrGC` 单元测试 —— 覆盖全部 11 个能力组。

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::gc::{GcEvent, GcHandleKind, GcKind, GcObserver, MagrGC, RcMagrGC, SnapshotCoverage};
use crate::metadata::{NativeData, TypeDesc, Value};

mod allocation;
mod collection;
mod config_stats;
mod cycle_collection;
mod events;
mod finalization;
mod object_model;
mod oom;
mod roots;
mod weak_refs;

pub(super) fn dummy_type_desc(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        // ... 完整 TypeDesc fields
    })
}
```

子模块开头:
```rust
use super::*;  // 继承 use 语句 + dummy_type_desc
```

### 与 [runtime-rust.md](../../../.claude/rules/runtime-rust.md) 测试组织规则的一致性

规则说:
> 每个实现模块 foo.rs 的测试放在同级 foo_tests.rs 中
> 在 foo.rs 末尾用条件编译引用：#[cfg(test)] mod foo_tests;

**子目录方案合规**: Rust 模块系统等价 — `foo_tests.rs` 与 `foo_tests/mod.rs` 都满足 `mod foo_tests;` 引用。文件超过 600 LOC 软限时按 topic 拆分到子目录是自然延伸（同 `jit/helpers/` 模式）。

### 与 split-typechecker-tests 对照

C# partial class 拆分（同包同类型）；Rust 子目录拆分（独立子模块）。两者目的相同（单文件 ≤ 软限）但机制差异源于语言：

| 维度 | C# (split-typechecker-tests) | Rust (本 spec) |
|---|---|---|
| 拆分机制 | partial class | 子目录 + mod |
| 共享代码 | 主文件保留 helpers | mod.rs 集中 |
| 命名 | `Tests.<Topic>.cs` | `<topic>.rs` |

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 baseline: `cargo test --manifest-path src/runtime/Cargo.toml` 全绿
- [ ] 1.2 grep `// ── ` 边界 + `#[test]` 数确认（已完成: 22 类 / 83 测试）

### 阶段 2: 创建 rc_heap_tests/ 目录与 9 个 topic 文件
- [ ] 2.1 `rc_heap_tests/mod.rs` — use 语句 + `dummy_type_desc` + 9 个 `mod xxx;`
- [ ] 2.2 `rc_heap_tests/allocation.rs` — §1
- [ ] 2.3 `rc_heap_tests/roots.rs` — §2 + Interp stack scanning + External root scanner
- [ ] 2.4 `rc_heap_tests/object_model.rs` — §4
- [ ] 2.5 `rc_heap_tests/collection.rs` — §5 + Auto-collect
- [ ] 2.6 `rc_heap_tests/cycle_collection.rs` — Phase 3c
- [ ] 2.7 `rc_heap_tests/finalization.rs` — §7-1 + §7 + §7-existing
- [ ] 2.8 `rc_heap_tests/weak_refs.rs` — §8 + §8.5 Handle table
- [ ] 2.9 `rc_heap_tests/events.rs` — §9 + §10
- [ ] 2.10 `rc_heap_tests/oom.rs` — §6-1
- [ ] 2.11 `rc_heap_tests/config_stats.rs` — §6 + §11

### 阶段 3: 改造 rc_heap.rs + 删除老文件
- [ ] 3.1 rc_heap.rs:943-945 删除 `#[path = "rc_heap_tests.rs"]`，保留 `#[cfg(test)] mod rc_heap_tests;`
- [ ] 3.2 删除 `src/runtime/src/gc/rc_heap_tests.rs`

### 阶段 4: 验证
- [ ] 4.1 `cargo build --manifest-path src/runtime/Cargo.toml` 无 warning
- [ ] 4.2 `cargo test --manifest-path src/runtime/Cargo.toml`: 全绿（rc_heap 测试 83 个不变）
- [ ] 4.3 `./scripts/test-vm.sh`: 全绿（VM golden 不受影响）
- [ ] 4.4 `wc -l src/runtime/src/gc/rc_heap_tests/*.rs` 全部 ≤ 300 LOC
- [ ] 4.5 `grep -rc "^#\[test\]" src/runtime/src/gc/rc_heap_tests/` 总和 = 83

### 阶段 5: 文档同步
- [ ] 5.1 `docs/review.md` 路线图 `split-rc-heap-tests` 状态 📋 → 🟢；§1.4 整体收口（C# + Rust 都已落地）
- [ ] 5.2 检查 `src/runtime/src/gc/README.md` 是否存在；存在则同步核心文件表

### 阶段 6: 归档 + 提交
- [ ] 6.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 6.2 `docs/spec/changes/split-rc-heap-tests/` → `docs/spec/archive/2026-05-07-split-rc-heap-tests/`
- [ ] 6.3 commit + push

---

## 备注

- **零行为变化**: 仅文件位置 + 共享 helper 提升
- **测试要求**（refactor 类型）: "确保已有测试仍覆盖；不得删除测试"——本 spec 不新增测试，83 测试全部保留
- **Git 识别**: 因为是子目录新建 + 单文件删除，git 不会识别为 rename。这是预期的，每个子文件 diff 是机械搬运
- **后续**: review.md §1.4 整体收口；下一项可推 `introduce-bound-visitor`（review.md §2.1 架构性 refactor）或 `split-large-codegen-files`（编译器线 P0）
