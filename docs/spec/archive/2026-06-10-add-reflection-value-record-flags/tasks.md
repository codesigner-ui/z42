# Tasks: Type.IsValueType / IsRecord（无格式 bump）

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统锁：`runtime` + `stdlib`（无 compiler、无格式变更）

## 进度概览
- [x] 阶段 1: runtime builtin
- [x] 阶段 2: stdlib
- [x] 阶段 3: 测试与文档
- [x] 阶段 4: GREEN

## 阶段 1: runtime（runtime）
- [x] 1.1 `reflection.rs`：`builtin_type_is_value_type`（CLASS_FLAG_STRUCT）+ `builtin_type_is_record`（CLASS_FLAG_RECORD），复用 `class_flag_set`
- [x] 1.2 `mod.rs`：注册 `__type_is_value_type` / `__type_is_record`

## 阶段 2: stdlib（stdlib）
- [x] 2.1 `Type.z42`：`IsValueType` / `IsRecord` extern bool getter

## 阶段 3: 测试与文档
- [x] 3.1 Golden `src/tests/types/type_flags.z42` 追加断言
- [x] 3.2 Dogfood `reflection.z42` [Test]
- [x] 3.3 Rust 单测 `reflection_tests.rs`
- [x] 3.4 `reflection.md`：API 表 + Deferred（type-flags 残留 IsValueType/IsRecord 标落地）

## 阶段 4: GREEN
- [x] 4.1 cargo build + dotnet build
- [x] 4.2 stdlib 重建（driver-direct）+ VM 重建（新 builtin）+ dist/release restage + embedding_hello（如需）
- [x] 4.3 cargo test --lib
- [x] 4.4 dotnet test（含 golden；**无 fixture 变更**）

## 备注
- **无格式 bump**：zbc 维持 1.14 / zpkg 0.16，不动 fixtures、不撞 port。
- struct/record 位由 add-reflection-type-flags 已写入；本变更纯读。
- IncrementalBuild count 不变（Type.z42 只改方法）。

## 验证报告（2026-06-10）
- ✅ cargo build (debug+release) + dotnet build — 无错
- ✅ cargo test --lib: **800/0**（含 type_flags_decode_struct_and_record + handle-less 单测）
- ✅ dotnet test: **1554/1554**（golden `type_flags.z42` 追加 struct/record/plain/handle-less 断言端到端跑通）
- ✅ **无格式 bump** —— zbc 维持 1.14 / zpkg 0.16，fixtures 未动；z42.core driver-direct 重建（新 Type.z42 方法）+ restage；VM 重建（新 builtin）
- ✅ 兑现 add-reflection-type-flags 一次写满 4 位的预留设计；不撞 port（已收敛）
