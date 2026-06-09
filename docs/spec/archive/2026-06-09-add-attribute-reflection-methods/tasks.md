# Tasks: 方法级 Attribute 反射（C3b）

> 状态：🟢 已完成（待归档）｜创建：2026-06-09｜类型：lang(codegen) + ir + vm + stdlib
> 占用：`compiler` + `runtime` + `stdlib`。机制镜像 C3a；parser/synthesizer 已共享。
> 验证：GoldenTests 1545/1545（含 methods.z42）。额外修：ZpkgWriter global SIGS；编译器跨文件属性 bug（Type.FindByType workaround）。

## 阶段 1: 编译器元数据
- [ ] 1.1 `IrModule.cs`：`IrFunction` 加 `List<IrAttributeRef>? Attributes`
- [ ] 1.2 IrGen：IrFunction 构造处从 `FunctionDecl.Attributes` 填（QualifyName 工厂名 + QualifyClassName type-name）
- [ ] 1.3 `ZbcWriter.BuildSigsSection`：每 func 追加 attr refs（u16 count + (type, factory) str-idx 对）+ `VersionMinor` 1.10→1.11
- [ ] 1.4 `ZbcReader` SIGS 读 attr refs（round-trip）
- [ ] 1.5 `ZpkgWriter.VersionMinor` 0.12→0.13
- [ ] 1.6 `dotnet build` 0 error

## 阶段 2: runtime
- [ ] 2.1 `bytecode.rs`：`Function`（cold）加 `custom_attributes: Box<[AttributeRef]>`
- [ ] 2.2 `zbc_reader.rs`：SIGS 读 attr refs + `ZBC_VERSION_MINOR` 1.11 / `ZPKG_VERSION_MINOR` 0.13
- [ ] 2.3 `reflection.rs`：`__method_custom_attributes` builtin（按 MethodInfo 的 qualified func name 查 Function.custom_attributes → 调工厂，复用 C3a 逻辑）
- [ ] 2.4 `mod.rs` 注册
- [ ] 2.5 `cargo build` 干净

## 阶段 3: stdlib
- [ ] 3.1 `Reflection/MethodInfo.z42`：`__attrCache` + `[Native] __customAttributes()` + `GetCustomAttributes()`（缓存）+ `GetAttribute(Type)`
- [ ] 3.2 stdlib workspace 重建 + flat view 同步

## 阶段 4: 测试 + 验证
- [ ] 4.1 `src/tests/attributes/methods.z42`（NEW golden）：方法 attribute + GetCustomAttributes + GetAttribute + 缓存
- [ ] 4.2 zbc/zpkg fixtures regen（1.11 / 0.13）
- [ ] 4.3 `dotnet test`（GoldenTests 权威）全绿
- [ ] 4.4 IncrementalBuildIntegrationTests 计数（MethodInfo.z42 无新增文件 → 不变；确认）

## 阶段 5: 文档 + 归档
- [ ] 5.1 zbc.md/zpkg.md changelog 1.11/0.13 + attributes.md（method-level 移出 Deferred）+ reflection.md + roadmap
- [ ] 5.2 归档 + ACTIVE.md 释放 + port re-port 目标更新 1.11 + commit + push

## 备注
- 元数据载体：SIGS section per-function（vs C3a 的 TYPE section per-class）。
- 顶层函数 attribute 同走此路径（synthesizer key `fn$<Func>`）。
