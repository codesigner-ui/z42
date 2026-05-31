# Tasks: extract `ScriptObject.type_desc` out of the Mutex

> 状态：🟢 已完成 | 创建：2026-05-31 | 完成：2026-05-31

## 进度概览
- [x] 阶段 1: lockless accessors（`GcRef::type_desc` / `type_desc_arc` / `type_args`）
- [x] 阶段 2: alloc_object 签名扩展 — **跳过**（borrow_mut 写 type_args 在 publish 之前，自然 release-acquire 保证可见性）
- [x] 阶段 3: GC mark 路径 — **无变更**（trace_children 仅走 slots；type_desc 是 Arc，不参与 GC mark trace）
- [x] 阶段 4: 调用点迁移（19 处 read + diag）
- [x] 阶段 5: JIT helpers — 已与阶段 4 一起完成
- [x] 阶段 6: 测试 + GREEN
- [x] 阶段 7: 文档同步 + 归档

## 阶段 1: lockless accessors

- [x] 1.1 `src/runtime/src/gc/refs.rs` — 新增 `GcRef::data_ptr_unlocked(&self) -> *mut T`，包装 `parking_lot::Mutex::data_ptr()`
- [x] 1.2 `src/runtime/src/metadata/types.rs` — `impl GcRef<ScriptObject>` 加 `type_desc(&self) -> &TypeDesc`、`type_desc_arc(&self) -> &Arc<TypeDesc>`、`type_args(&self) -> &[String]`
- [x] 1.3 `cargo build --release` 通过

## 阶段 2: alloc_object 签名扩展

- [x] 2.1 决策修订：不扩展签名。type_args 的 borrow_mut 写发生在 ObjNew 内部 publish 之前；Mutex unlock 的 release 与下游访问的 acquire 配对，read-via-data_ptr 在 publish 之后看到的 type_args 永远是已写好的版本。跳过此阶段避免触及 8 个 alloc_object 调用点

## 阶段 3: GC mark

- [x] 3.1 决策修订：mark 路径无变更。`Value::trace_children` 只 visit slots（heap refs）；type_desc 是 `Arc<TypeDesc>`，不是 GcRef，不参与 GC mark trace。本变更对 mark phase invisible

## 阶段 4: 调用点迁移（19 处）

- [x] 4.1 `src/runtime/src/interp/exec_vcall.rs` — 2 处（PIC scan 的 `type_desc.id.0` + ToString fallback 的 `type_desc.clone()`）
- [x] 4.2 `src/runtime/src/interp/exec_object.rs` — 2 处（is_instance / as_cast 用 `&type_desc().name`）
- [x] 4.3 `src/runtime/src/interp/dispatch.rs` — 1 处（obj_to_string）
- [x] 4.4 `src/runtime/src/interp/mod.rs` — 2 处（exception_class_and_message / find_handler）
- [x] 4.5 `src/runtime/src/jit/helpers/control.rs` — 1 处（match_catch_type）
- [x] 4.6 `src/runtime/src/jit/helpers/object.rs` — 2 处（is_instance / as_cast）
- [x] 4.7 `src/runtime/src/jit/helpers/value.rs` — 1 处（jit_to_str ToString lookup）
- [x] 4.8 `src/runtime/src/jit/helpers/vcall.rs` — 1 处（PIC fast path）
- [x] 4.9 `src/runtime/src/corelib/convert.rs` — 1 处（value_to_str）
- [x] 4.10 `src/runtime/src/corelib/object.rs` — 2 处（builtin_obj_get_type / builtin_obj_to_str）
- [x] 4.11 `src/runtime/src/corelib/tests.rs` — 1 处
- [x] 4.12 `src/runtime/src/gc/arc_heap.rs` — 2 处（debug_validate_invariants diag + type_name_of snapshot helper）
- [x] 4.13 `src/runtime/src/exception/mod.rs` — 1 处（uncaught-exception formatter）

## 阶段 5: JIT helpers

- [x] 已与阶段 4 一起完成（4.5–4.8）

## 阶段 6: 测试 + GREEN

- [x] 6.1 `cargo test --release --lib` — 693/693 + 21/21 pass
- [x] 6.2 `./scripts/test-vm.sh` — 168 interp + 160 jit = 328/328 pass
- [x] 6.3 `./scripts/test-cross-zpkg.sh` — 2/2 pass
- [x] 6.4 顺手修了一个 stale assert（`zbc_version_constants_pinned` 还在断言 minor=8，但 parallel session 已 bump 到 9）

## 阶段 7: 文档同步 + 归档

- [x] 7.1 `docs/design/runtime/gc.md` — 加 "extract-typedesc-from-mutex" 子节描述 lockless accessor + safety + 对照 CoreCLR/HotSpot
- [x] 7.2 `docs/design/runtime/vm-architecture.md` — IC 段加 note 说 PIC scan 的 type_id 读不再 lock，为 future PIC inline 铺路
- [x] 7.3 归档：`docs/spec/changes/extract-typedesc-from-mutex/` → `docs/spec/archive/2026-05-31-extract-typedesc-from-mutex/`

## 备注

- 没有 struct split：保持 `ScriptObject` 单 struct，只在访问路径上做区分（accessor vs borrow guard）。比 design.md 草案的 split-into-header+body 方案更小、更聚焦
- `borrow_mut().type_args = ...` 这种写入路径保持不变（ObjNew 仍这样写）。我们只优化了 read 路径
- 后续的 `jit-pic-inline` spec 现在可以独立推进 —— receiver type_id 已经是 3 条 native load 可拿到的值

## References

- proposal.md
- design.md
- specs/script-object-layout/spec.md
