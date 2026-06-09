# Proposal: Type.IsAbstract / IsSealed — 类修饰符反射（zbc 1.12）

## Why

反射 MVP 暴露了字段/方法/属性/attribute，但 `Type` 仍无法回答"这是抽象类吗？sealed 吗？"——`reflection-future-type-flags`。类修饰符（`abstract` / `sealed`）在编译期 `ClassDecl.IsAbstract` / `IsSealed` 已知，但**未持久化进 zbc TYPE section**（VM 加载的 `TypeDesc` 无 class flag），故运行期反射拿不到。

这是个 **zbc 格式变更**（TYPE section 加 flags 字节 → 1.11→1.12，联动 zpkg 0.13→0.14）。

**时机协调**：`port-z42c-zbc-writer` 自举移植此刻正为 1.11 重新镜像（byte-identical gate 已红）。现在追加 1.12 让该 port 在**同一 re-port 周期**直接对齐 1.12，不多一轮；等其 1.11 完工后再 bump 才会逼出第二轮 re-port。User 已确认采此项（接受 port 对齐 1.12）。

## What Changes

- zbc TYPE section 每类追加 `flags: u8`（bit0 abstract / bit1 sealed / bit2 struct / bit3 record）。**捕获 4 位**（struct/record 现不暴露 API，但写进 wire 以免将来 `IsValueType`/`IsRecord` 再 bump 格式）。
- `IrClassDesc` 加 `IsAbstract` / `IsSealed` / `IsStruct` / `IsRecord`；`IrGen.EmitClassDesc` 从 `ClassDecl` 填充。
- ZbcWriter 写 flags 字节 + `VersionMinor 11→12`；ZbcReader / runtime `read_type` 读；`ClassDesc` + `TypeDesc` 携 `class_flags: u8`；`build_type_registry` 透传。
- stdlib：`Type.IsAbstract` / `Type.IsSealed`（`[Native]` extern bool getter）。**不加 `IsStatic`**——z42 无 `static class` 概念（`ClassDecl` 无该修饰符）。
- 版本 bump 全套：zbc 1.12 / zpkg 0.14 + reader 常量 + changelog + fixture regen。
- 文档 + golden + dogfood [Test] + Rust 单测。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrClassDesc` 加 4 个 flag 字段 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitClassDesc` 从 ClassDecl 填 flags |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | TYPE section 写 flags 字节 + `VersionMinor 11→12` |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | `ReadTypeSection` 读 flags 字节 |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor 13→14`（联动注释更新内嵌 zbc 1.12）|
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `ClassDesc.class_flags: u8` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `read_type` 读 flags + `ZBC_VERSION_MINOR 11→12` + `ZPKG_VERSION_MINOR 13→14` |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDesc.class_flags: u8` |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `build_type_registry` 透传 flags ClassDesc→TypeDesc |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_is_abstract` / `builtin_type_is_sealed` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册两个 builtin |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsAbstract` / `IsSealed` extern bool getter |
| `docs/design/runtime/zbc.md` | MODIFY | 1.12 minor changelog |
| `docs/design/runtime/zpkg.md` | MODIFY | 0.14 minor changelog |
| `docs/design/language/reflection.md` | MODIFY | API 表 + 实现原理 + Deferred 标落地 |
| `src/tests/types/type_flags.z42` | NEW | golden：abstract/sealed/plain 反射 |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | dogfood [Test] |
| `src/runtime/src/corelib/reflection_tests.rs` | MODIFY | lenient + flags 单测 |
| `src/tests/zbc-format/generate-fixtures.sh` | RUN | regen 6 zbc fixtures（格式 delta）|
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN | regen zpkg fixtures |

**只读引用**：`src/compiler/z42.Syntax/Parser/Ast.cs`（ClassDecl flags）；现有 ZbcWriter/Reader TYPE 布局；`build_method_info`（builtin 模板）。

## Out of Scope

- `Type.IsStatic`——z42 无 `static class`（若将来引入，bit 已留，加 API + emit 即可）。
- `IsValueType`（struct）/ `IsRecord` 的 stdlib API——**bit 已写进 wire**，将来纯 stdlib 加 getter，不再 bump 格式。
- 接口/枚举类型标志（`IsInterface`/`IsEnum`）——独立，按需再做。
- static fields 纳入 GetFields、field/param attribute targets——各自独立的格式变更。

## Open Questions

- [ ] 无（设计闭合，见 design.md）。
