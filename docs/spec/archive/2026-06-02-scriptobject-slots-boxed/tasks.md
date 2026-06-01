# Tasks: ScriptObject.slots Vec<Value> → Box<[Value]>

> 状态：🟢 已完成 | 创建：2026-06-02 | 完成：2026-06-02 | 类型：refactor（内存优化，对外行为不变）
> 来源：[`docs/review.md`](../../../review.md) Part 5 P3 E2.P6

## 变更说明

`ScriptObject.slots` 从 `Vec<Value>` 改为 `Box<[Value]>`。

slot 数量在 `alloc_object` 时确定（= `TypeDesc.fields.len()`），之后只**索引读写**，
从不 push / pop / resize。`Box<[Value]>` 比 `Vec<Value>` 省 8 B/object（无
`capacity` 字段），其余 ops（index / iter / len / `&mut [Value]`）API 兼容。

## 原因

review.md E2.P6 / E5.4 list ScriptObject header 瘦身为 P3，type_args 部分已
完成（Box<[String]>），剩余 slots 由 Vec → Box<[T]> 是相同结构的另一半收益。
跨 stdlib 所有对象生效，乘数效应明显。

## 文档影响

- `docs/review.md` E2.P6 / E5 表格状态更新
- 不动 wire 格式 / 语言语义 / IR

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/types.rs` | MODIFY | `slots: Vec<Value>` → `Box<[Value]>` |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `alloc_object` 内部 `slots.into_boxed_slice()` 边界转换；trait 签名保持 `Vec<Value>`（callers 无需改） |
| `src/runtime/src/metadata/types_tests.rs` | MODIFY | 2 处 `slots: vec![...]` → `.into_boxed_slice()` 或 `Box::new([...])` |
| `docs/review.md` | MODIFY | E2.P6 / E5 状态标 🟡 / ✅ |

只读：`MagrGC` trait 签名 (`heap.rs:61` — 保持 `slots: Vec<Value>`)

## 任务

- [x] 0.1 NEW `docs/spec/changes/scriptobject-slots-boxed/tasks.md`
- [x] 1.1 MODIFY `types.rs` slots 字段类型
- [x] 1.2 MODIFY `arc_heap.rs` alloc_object `.into_boxed_slice()` 边界 + 2 处 `capacity()` → `len()`（size estimate）
- [x] 1.3 MODIFY `types_tests.rs` 2 处 init 修正 + `snapshot.rs` slots clone 类型注解
- [x] 1.4 VERIFY `cargo build --release` 干净
- [x] 1.5 VERIFY `./scripts/test-all.sh` 全绿（6 stages, scope=full, stdlib 258 file all pass）
- [x] 1.6 MODIFY `review.md` 标 🟡 step-2 done
- [x] 1.7 归档 + commit + push
