# Tasks: 用户自定义 Attribute + 反射（C3a，class-level）

> 状态：🟢 已完成（class-level）｜创建：2026-06-09｜类型：lang + zbc 格式 + vm + stdlib
> 占用子系统：`compiler` + `runtime` + `stdlib`（归档时释放）
> 协调：改 .zbc/.zpkg 格式（1.10 / 0.12），port-z42c-zbc-writer 落地后 re-port（User 裁决 proceed）
> 增量：class-level 本 change（commit 56d9cefb + 1377bfdb）；method-level → C3b（add-attribute-reflection-methods）
> 契约强制：工厂返回类型 `Attribute` → typecheck 顺带强制（非独立 validator）；专用诊断 + negative 测试推后

## 进度概览
- [ ] 阶段 1: stdlib 基类 + 解析 + AST
- [ ] 阶段 2: typecheck 绑定
- [ ] 阶段 3: codegen 工厂合成 + 元数据 emit
- [ ] 阶段 4: zbc/zpkg 格式 + 版本 bump
- [ ] 阶段 5: runtime 载入 + 反射 builtin + 缓存
- [ ] 阶段 6: stdlib 反射 API
- [ ] 阶段 7: 测试
- [ ] 阶段 8: 验证 + 文档 + 归档

## 阶段 1: 基类 + 解析 + AST
- [ ] 1.1 `z42.core/src/Attribute.z42`（NEW）：`public class Attribute {}`（Std 命名空间）
- [ ] 1.2 `Ast.cs`：`AttributeApp(string Name, List<Argument> Args, Span)`；`ClassDecl` + `FunctionDecl` 加 `List<AttributeApp>? Attributes`
- [ ] 1.3 `TopLevelParser.Helpers.cs::TryParseAttribute`：非 Native/Test 名 → 归为通用 `AttributeApp`
- [ ] 1.4 `TopLevelParser.Types.cs`：class 前（当前丢弃）+ method 前收集 attribute → 挂 AST
- [ ] 1.5 `dotnet build` 0 error；AttributeParseTests 绿

## 阶段 2: typecheck 绑定
- [ ] 2.1 `AttributeBinder.cs`（NEW）：Name → attribute 类（须派生 Std.Attribute，E0920）
- [ ] 2.2 ctor 解析：复用 named-arg + 默认值绑定；无匹配 → E0921
- [ ] 2.3 常量参数校验：字面量 / enum 成员 / typeof，否则 E0922
- [ ] 2.4 `SymbolCollector.Classes.cs`：attribute 挂 class 符号
- [ ] 2.5 AttributeBindTests（E0920/0921/0922）绿

## 阶段 3: codegen 工厂合成 + 元数据
- [ ] 3.1 `AttributeFactorySynthesizer.cs`（NEW）：每应用合成 `Std.Attribute __attr_factory_N() { return new T(args); }` → cu.Functions
- [ ] 3.2 记录 owner 声明 → [(TypeName, FactoryFunc)] 映射
- [ ] 3.3 `IrGen.Generate.cs`：映射写入 ExportedClassDef/MethodDef.Attributes
- [ ] 3.4 工厂函数正常 IR + func_index 验证（dump 检查）

## 阶段 4: zbc/zpkg 格式 + bump
- [ ] 4.1 `ExportedTypes.cs`：`ExportedAttributeRef(TypeName, FactoryFunc)` + ClassDef/MethodDef 加 `Attributes`
- [ ] 4.2 `ZbcWriter.cs`：序列化 attribute refs + `VersionMinor` 9→10
- [ ] 4.3 `ZpkgWriter.cs`：TSIG attribute 持久化 + `VersionMinor` 11→12
- [ ] 4.4 runtime `zbc_reader.rs`：`ZBC_VERSION_MINOR` 同步 + 读 attribute refs
- [ ] 4.5 version-bumping.md checklist 逐点核对（无遗漏同步点）

## 阶段 5: runtime 载入 + 反射 + 缓存
- [ ] 5.1 `metadata/types.rs`：`TypeDescCold.custom_attributes: Box<[AttributeRef]>` + method 侧
- [ ] 5.2 `reflection.rs::builtin_type_custom_attributes`：查 factory func_index → 调用（无参）→ Attribute 实例
- [ ] 5.3 缓存：首次实例化后缓存（挂 type 对象槽），后续返回同一实例
- [ ] 5.4 `corelib/mod.rs`：注册 `__type_custom_attributes`（+ method 版）
- [ ] 5.5 `cargo build` debug+release 干净

## 阶段 6: stdlib 反射 API
- [ ] 6.1 `Type.z42`：`[Native("__type_custom_attributes")] extern Attribute[] GetCustomAttributes();`
- [ ] 6.2 `Type.z42`：`Attribute? GetAttribute(Type t)`（z42 实现：遍历 + FullName 比较）
- [ ] 6.3 `Reflection/MethodInfo.z42`：同上
- [ ] 6.4 `xtask build stdlib z42.core` 编过

## 阶段 7: 测试
- [ ] 7.1 `src/tests/attributes/basic.z42`（NEW golden）：声明 + class/method 应用 + 读字段 + 缓存一致 + GetAttribute
- [ ] 7.2 `z42.core/tests/attributes.z42`（NEW `[Test]`）
- [ ] 7.3 cross-zpkg：包 A 定义+应用，包 B 反射（持久化）
- [ ] 7.4 golden `.zbc` regen（格式变）

## 阶段 8: 验证 + 文档 + 归档
- [ ] 8.1 `dotnet test`（C# GoldenTests 权威 + 全量）
- [ ] 8.2 `xtask test`（vm/cross-zpkg/lib；刷新 `.z42/bin/z42vm`）
- [ ] 8.3 spec scenarios 逐条确认
- [ ] 8.4 `docs/design/language/attributes.md`（NEW）+ `zbc.md` changelog + `reflection.md`（C3 移出 Deferred）+ `roadmap.md`
- [ ] 8.5 归档 + ACTIVE.md 释放 + 通知 port-z42c-zbc-writer re-port + commit + push

## 备注
- **大特性**：跨 parser/typecheck/codegen/zbc 格式/runtime/stdlib。User 选 one-combined-spec；如某阶段实际量 >1.5x 预估 → 停下报告（中断条件 7）。
- **格式 bump 协调**：完成后通知并行 port-z42c-zbc-writer 按新格式 re-port（ACTIVE.md 已记）。
- **5 项改进**：#1 无后缀（2.1 解析按真实名）、#2 单 ctor（2.2）、#3 缓存（5.3）、#4 不可变（约定）、#5 target 推后（不实现）。
