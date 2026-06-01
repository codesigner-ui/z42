# Tasks: TypeDesc field/method name → NameIndex (linear scan)

> 状态：🟢 已完成 | 创建：2026-06-01 | 完成：2026-06-02 | 类型：refactor（性能 + 内存优化，不改对外行为）
> 来源：[`docs/review.md`](../../../review.md) Part 2 C4 P1 / C5 P1

## 变更说明

把 `TypeDesc.field_index: HashMap<String, usize>` + `TypeDesc.vtable_index:
HashMap<String, usize>` 替换为新类型 `NameIndex`（`Vec<(Box<str>, usize)>` +
linear scan）。

review.md C4 / C5 P1 指出：FieldIC / VCallIC（已上线 + PIC 也已上线）能拦
住大部分 hot path，但 IC miss 时仍走 `HashMap<String, usize>.get(name)`
即 hash + string compare —— megamorphic / 首次类型仍付该 cost。z42 stdlib
+ 用户代码里典型 class 字段 / 方法数 ≤ 16，**linear scan ≤16 项的
`Box<str>` ≡ &str 比 HashMap 探测 + 字符串 compare 快**（cache locality 友好；
无 hash 函数计算；分支预测对小循环友好）。同时 `Box<str>` 比 `String` 省
8 B/entry（无 capacity 字段）。

## 原因

review.md Part 2 C4 / C5 共同的 P1 改造路径之一，IC + PIC 落地后的下一步
unlock。**不动 wire 格式**（zbc / zpkg 无 bump），是纯运行时数据结构调整。

## 文档影响

- 无新语言行为 / IR / VM 语义变化 → `docs/design/language/` 不动
- `docs/design/runtime/vm-architecture.md` 加一段说明 NameIndex 取代
  HashMap 的 trade-off（对外不可见但 reviewer 该理解为何 TypeDesc 不用
  HashMap）
- review.md Part 2 C4 / C5 完成后标 ✅

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/name_index.rs` | NEW | `NameIndex` 类型 + HashMap-compat API |
| `src/runtime/src/metadata/name_index_tests.rs` | NEW | 单元测试：get / insert / iter / clone / FromIterator / 边界 |
| `src/runtime/src/metadata/mod.rs` | MODIFY | 加 `pub mod name_index;` + re-export `NameIndex` |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDesc.field_index` + `vtable_index` 类型改 `NameIndex` |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `let field_index: HashMap<...> = ... .collect()` 改 `NameIndex` |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `td.field_index.clone()` 处只换类型，API 保持 |
| `src/runtime/src/metadata/types_tests.rs` | MODIFY | 测试 init 用 `NameIndex::new()` 替 `HashMap::new()` |
| `src/runtime/src/metadata/loader_tests.rs` | MODIFY | 同上；`field_index.get("name")` API 保持 |
| `src/runtime/src/interp/mod.rs` | MODIFY | `field_index.get(...).copied()` API 保持 |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | `vtable_index.get("ToString")` API 保持 |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | API 保持 |
| `src/runtime/src/interp/exec_vcall.rs` | MODIFY | API 保持 |
| `src/runtime/src/jit/helpers/value.rs` | MODIFY | API 保持 |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | API 保持 |
| `src/runtime/src/corelib/object.rs` | MODIFY | `field_index.insert("name".to_string(), 0)` API 保持 |
| `src/runtime/src/corelib/gc.rs` | MODIFY | 同上 |
| `src/runtime/src/corelib/tests.rs` | MODIFY | 测试 init |
| `src/runtime/src/exception/mod.rs` | MODIFY | 出错路径 |
| `src/runtime/src/exception/tests.rs` | MODIFY | 测试 init |
| `src/runtime/src/gc/snapshot_tests.rs` | MODIFY | 测试 init |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | 测试 init |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 测试 init |
| `src/runtime/benches/gc_cycle_bench.rs` | MODIFY | bench init |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 加一段 NameIndex trade-off |
| `docs/review.md` | MODIFY | C4 / C5 标 ✅ |

只读引用：
- `src/runtime/src/metadata/resolver.rs` — 内部注释提及 `field_index.get(name)`，不需改

## 设计要点

NameIndex 的 API 设计为 **`HashMap<String, usize>` 兼容子集**，目标是让消费
代码"零改动"（只需换类型声明），不必改一行 `.get()` 调用：

```rust
pub struct NameIndex {
    entries: Vec<(Box<str>, usize)>,
}

impl NameIndex {
    pub fn new() -> Self;
    pub fn len(&self) -> usize;
    pub fn is_empty(&self) -> bool;
    pub fn get(&self, name: &str) -> Option<&usize>;          // 兼容 HashMap
    pub fn insert(&mut self, name: String, slot: usize) -> Option<usize>;
    pub fn iter(&self) -> impl Iterator<Item = (&str, usize)>;
    pub fn contains_key(&self, name: &str) -> bool;
}

impl Default / Clone / Debug for NameIndex;
impl FromIterator<(String, usize)> for NameIndex;
```

`get` 返回 `Option<&usize>` 而不是 `Option<usize>` —— 关键是这样 caller 写
`field_index.get(name).copied()` 仍能编译。代价：linear scan 把 hit 元素的
slot 引用借出（lifetime 跟 `&self` 一致），完全 ergonomic。

## 任务

- [x] 0.1 NEW `docs/spec/changes/add-name-index-typedesc/tasks.md`
- [ ] 1.1 NEW `src/runtime/src/metadata/name_index.rs`
- [ ] 1.2 NEW `src/runtime/src/metadata/name_index_tests.rs`
- [ ] 1.3 MODIFY `metadata/mod.rs` add `pub mod name_index; pub use name_index::NameIndex;`
- [ ] 1.4 MODIFY `metadata/types.rs` swap 2 HashMap → NameIndex
- [ ] 1.5 MODIFY 其余消费 / 测试 init 文件（API 兼容，主要是类型声明）
- [x] 1.1 NEW `src/runtime/src/metadata/name_index.rs` (NameIndex + 13 unit tests)
- [x] 1.2 NEW `src/runtime/src/metadata/name_index_tests.rs`
- [x] 1.3 MODIFY `metadata/mod.rs` + `metadata/types.rs` 切换 NameIndex
- [x] 1.4 MODIFY 全部消费方（loader / interp / jit / corelib / exception / gc / tests / bench）
- [x] 1.5 VERIFY: `cargo build --manifest-path src/runtime/Cargo.toml --release` 干净
- [x] 1.6 VERIFY: `cargo test --manifest-path src/runtime/Cargo.toml` 全过
- [x] 1.7 VERIFY: `./scripts/test-all.sh` 全绿（6 stages, scope=full, stdlib 257 files / 22 libs all pass）
- [x] 1.8 MODIFY `docs/design/runtime/vm-architecture.md` 加 NameIndex 注解
- [x] 1.9 MODIFY `docs/review.md` C4 / C5 标 🟡 Step 1 done (剩余 wire format 路径留待后续 spec)
- [x] 1.10 归档 `docs/spec/changes/add-name-index-typedesc/` → `docs/spec/archive/2026-06-02-add-name-index-typedesc/`
- [x] 1.11 commit + push

## 备注

- 不动 zbc / zpkg 格式 → 不需要 minor bump → 不需要 fixture regen
- 不破坏 Send / Sync —— `Vec<(Box<str>, usize)>` 是 Send + Sync
- review.md C4 P3 + #3 sub-item（`Instruction::FieldGet.field_name: String → field_id: u32`）**不在本 spec**，那是 wire format 改动，需 zbc 1.x → 1.y bump + 单独 spec
