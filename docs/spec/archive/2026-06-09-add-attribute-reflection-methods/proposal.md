# Proposal: 方法级 Attribute 反射（C3b）

> 状态：🟢 已实施（待归档）｜创建：2026-06-09｜类型：lang(codegen) + ir(zbc 格式) + vm + stdlib
> 占用子系统：`compiler` + `runtime` + `stdlib`（[ACTIVE.md](../ACTIVE.md)）
> **延续**：C3a（class-level，archive/2026-06-09-add-attribute-reflection）的方法级补全。机制完全镜像 C3a，仅元数据载体从 TYPE section（per-class）改为 SIGS section（per-function）。
>
> **实施记录（2026-06-09）**：验证 GoldenTests 1545/1545（含新 `methods.z42` golden）。
> - **额外修复**：ZpkgWriter 有独立的 **global SIGS** builder（`ZpkgWriter.BuildSigsSection`，非 ZbcWriter 的），也需同步加 attr refs，否则 z42.core.zpkg 解析错位。
> - **发现编译器 bug（已 workaround，记 attributes.md Deferred `attribute-future-crossfile-property-resolution`）**：z42.core 同包内**跨文件**访问 extern `{ get; }` 属性被误解析为字段（→ null）。`MethodInfo.GetAttribute` 里 `someType.FullName` 失配。Workaround：把 FullName 比较逻辑放进 `Type.z42` 的静态 `Type.FindByType(...)`，两处 GetAttribute 都委托它（MethodInfo 加 `using Std` 后非限定调用）。

## Why

C3a 让 class 上的 `[Attr]` 可反射。method 上的 `[Attr]`（`[Doc] void M()`）已被 parser 收集 + synthesizer 合成工厂（与 C3a 共享代码），但**未写入元数据 → 反射读不到**。C3b 把方法 attribute refs 持久化进 SIGS section + 运行期载入 + `MethodInfo.GetCustomAttributes()` 暴露。

## What Changes

- `[Attr(args)]` 标注方法（含顶层函数）→ `MethodInfo.GetCustomAttributes() : Attribute[]` / `GetAttribute(Type)` 返活实例（缓存）。
- 机制同 C3a：工厂函数（已合成）+ SIGS section 记 (type-name, factory-func) refs + 运行期调工厂构造活实例。

## 已就绪（C3a 共享，无需改）

- **parser**：方法 attribute 收集进 `FunctionDecl.Attributes`（TopLevelParser.Types.cs 方法循环）。
- **synthesizer**：`AttributeFactorySynthesizer.ProcessFunction` 对每方法合成工厂 + 填 `FactoryFunc`（key `mth$<Class>$<Method>` / `fn$<Func>`）。
- **stdlib 基类**：`Std.Attribute` + factory 返回类型 = Attribute 的契约强制。
- **runtime 工厂调用**：`run_returning` re-entrancy + 缓存模式。

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrFunction` 加 `List<IrAttributeRef>? Attributes`（复用 C3a 的 IrAttributeRef）|
| `src/compiler/z42.Semantics/Codegen/IrGen.*.cs` | MODIFY | IrFunction 构造处填 Attributes（从 FunctionDecl.Attributes，QualifyName 工厂名）|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `BuildSigsSection` 每 func 加 attr refs；`VersionMinor` 1.10→1.11 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | SIGS 读 attr refs |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 0.12→0.13（联动）|
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `Function`（或 cold）加 `custom_attributes: Box<[AttributeRef]>` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | SIGS 读 attr refs + `ZBC_VERSION_MINOR` 1.11 + `ZPKG_VERSION_MINOR` 0.13 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `build_method_info` 加 attr；`__method_custom_attributes` builtin（或复用 __type_custom_attributes 逻辑）|
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 builtin |
| `src/libraries/z42.core/src/Reflection/MethodInfo.z42` | MODIFY | `GetCustomAttributes()` + `GetAttribute(Type)` + `__attrCache` |
| `src/tests/attributes/methods.z42` | NEW | golden：方法 attribute + 反射 |
| `docs/design/runtime/zbc.md` + `zpkg.md` | MODIFY | changelog 1.11 / 0.13 |
| `docs/design/language/attributes.md` | MODIFY | method-level 从 Deferred 移除 |
| `docs/design/language/reflection.md` + `docs/roadmap.md` | MODIFY | C3b 落地 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 释放锁 |

**只读引用**：archive/2026-06-09-add-attribute-reflection/design.md（C3a 机制）；C3a 的 IrClassDesc/TYPE-section/builtin_type_custom_attributes（直接镜像）。

## Out of Scope

- field / parameter targets（仍 Deferred）。
- 专用 E09xx 诊断 + negative 测试 harness（C3a 同款 Deferred）。

## 协调

zbc 1.11 / zpkg 0.13 bump → port-z42c-zbc-writer re-port 目标更新到 1.11（与 C3a 1.10 合并为一次 re-port）。
