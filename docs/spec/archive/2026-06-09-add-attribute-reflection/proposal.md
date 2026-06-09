# Proposal: 用户自定义 Attribute + 反射（C3）

> 状态：🟢 已实施（class-level，C3a）｜创建：2026-06-09｜类型：lang（语法 + typecheck + zbc 格式 + vm + stdlib）
> 占用子系统：`compiler` + `runtime` + `stdlib`（[ACTIVE.md](../ACTIVE.md)）
>
> **实施记录 / 增量划分（2026-06-09，User 裁决 class-first）**：
> - **C3a（本 change，已落地 + 归档）**：class-level——`[Foo(args)]` 标注 class → `Type.GetCustomAttributes()` / `GetAttribute(Type)` 返活实例。commit `56d9cefb`（管线 + zbc 格式 + runtime + stdlib + golden，GoldenTests 1544/1544）+ `1377bfdb`（契约用工厂返回类型 `Attribute` 强制，免独立 validator）。
> - **C3b（跟随 change `add-attribute-reflection-methods`）**：method-level——`[Doc] void M()` → `MethodInfo.GetCustomAttributes()`（平行的 function-sigs 元数据路径）。
> - **契约强制方式变更**：原计划独立 AttributeBinder（E0920/E0922），实施改为**工厂返回类型 = `Attribute` 基类** → 普通 typecheck 顺带强制（非 attribute 类→上转型失败；非常量参数→工厂作用域未知标识符）。专用 E09xx 诊断 + negative 测试 harness 推后（attributes.md Deferred）。

## Why

0.3.x C 主线 C3，反射 MVP 收尾。C1（GetType）、C2（typeof）让类型元数据可反射，但**属性（attribute）仍是封闭内建集**——`[Test]`/`[Benchmark]`/`[Native]` 写死在编译器里（[TopLevelParser.Helpers.cs](../../../../src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs)），用户无法定义自己的 attribute，也无法反射读取。C3 引入**用户自定义 attribute 类 + 应用语法 + 反射读取**，补齐反射 MVP 的最后一块。

设计取向：**不照搬 C#，修正其已知缺陷**（User 2026-06-09 裁决采纳全部 5 项改进）。

## What Changes

- **声明**：attribute 是普通类继承 `Std.Attribute`——`class Route : Attribute { ... }`。无 `attribute` 关键字。
- **应用**：`[Name(pos, named: v)]` 标注 **class / method**（MVP 目标）。参数限编译期常量（字面量 / enum 成员 / `typeof`）。
- **反射**：`Type.GetCustomAttributes() : Attribute[]` / `GetAttribute(Type) : Attribute?`，`MethodInfo` 同。返回**活实例**（非字符串/字典）。
- **持久化**：attribute 应用记入 .zbc/.zpkg 元数据（格式 bump），跨 zpkg 可反射。

### 对 C# attribute 的 5 项改进（User 已确认全采纳）

| # | C# 缺陷 | z42 改进 |
|---|---------|---------|
| 1 | `Attribute` 后缀魔法（`class FooAttribute` 写作 `[Foo]`，两种拼法）| **无后缀约定**：`class Route : Attribute` 即写作 `[Route]`，单一拼法，零改名 |
| 2 | 双初始化路径（positional→ctor，named→public 字段直写）| **单一 ctor 路径**：全部参数走构造器（复用已有 named-arg + 默认值 [add-named-arguments](../../../../src/compiler/z42.Syntax/Parser/Ast.cs)），无旁路字段写 |
| 3 | 每次 `GetCustomAttributes()` 重新分配新实例 + 返回 `object[]` | **缓存单例**：首次反射时实例化一次并缓存，后续返回同一实例 |
| 4 | 实例可变（正是 #3 必须复制的原因）| **不可变实例**（ctor 内一次写定）：使 #3 缓存安全。MVP 用约定 + 缓存，不强制 init-only（后补）|
| 5 | `AttributeUsage` 是元属性（自循环 + 反直觉默认）| **MVP 不做 target 校验**（attribute 可标任意支持位）；将来加时做**一等声明子句**而非元属性 |

> **副带改进（factory thunk 自然产生）**：attribute 参数被编译进工厂函数体（见 design.md），z42 **无需** C# 的元数据参数 blob 编解码——工厂函数本身即"序列化形式"，元数据更小更简单。

## 实现要点（详见 design.md）

**活实例如何待在 0.3.x 边界内**（不碰 0.5.x 才做的 `Activator`/`Method.Invoke`）：编译期为每个 `[Foo(args)]` 合成一个**无参工厂函数** `Std.Attribute __attr_factory_N() { return new Foo(args); }`（复用 [BenchmarkDesugar](../../../../src/compiler/z42.Semantics/Codegen/BenchmarkDesugar.cs) 合成 FunctionDecl 模式），元数据记 `(attribute 类型名, 工厂 func 引用)`。反射时调工厂（一次，缓存）。全程已知类型 + 已知 ctor + 常量参数，编译期可解析，无需运行时泛型实例化。

## 与 port-z42c-zbc-writer 的协调（User 裁决：proceed + re-port）

C3 改 .zbc/.zpkg 元数据格式（attribute 持久化 + `VersionMinor` bump），与活跃的 `port-z42c-zbc-writer`（byte-identical 镜像 ZbcWriter.cs）冲突。User 2026-06-09 裁决：**C3 现在推进，并行 port 在 C3 落地后按新格式重新镜像**（接受 re-port，byte-identical gate 暂红）。已记 ACTIVE.md `z42c` 行。

## Scope（允许改动的文件）

### 编译器（compiler）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `AttributeApp(string Name, List<Argument> Args, Span)`；`ClassDecl` + `FunctionDecl` 加 `List<AttributeApp>? Attributes` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `TryParseAttribute` 识别通用用户 attribute（非 Native/Test 即归此）|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | class 前 + method 前收集 attribute（当前 class 前被静默丢弃）→ 挂到 AST |
| `src/compiler/z42.Semantics/TypeCheck/AttributeBinder.cs` | NEW | 解析 attribute 类（须派生 `Std.Attribute`）+ ctor 解析 + 常量参数校验（E09xx）|
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs` | MODIFY | attribute 信息挂到 class 符号 |
| `src/compiler/z42.Semantics/Codegen/AttributeFactorySynthesizer.cs` | NEW | 合成无参工厂 FunctionDecl + 记录 应用→工厂 映射 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs` | MODIFY | attribute 元数据写入 ExportedClassDef/MethodDef |
| `src/compiler/z42.IR/ExportedTypes.cs` | MODIFY | 新增 `ExportedAttributeRef(string TypeName, string FactoryFunc)`；`ExportedClassDef`/`ExportedMethodDef` 加 `Attributes` |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 序列化 attribute refs + `VersionMinor` bump（9→10）|
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | TSIG attribute 持久化 + `VersionMinor` bump（11→12）|

### 运行时（runtime）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDesc`(cold) + 函数描述加 `custom_attributes: Vec<AttributeRef>` + 缓存槽 |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 读 attribute refs + `ZBC_VERSION_MINOR` 同步 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_custom_attributes`（调工厂 thunk + 缓存）+ method 版 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 builtins |

### 标准库（stdlib）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/libraries/z42.core/src/Attribute.z42` | NEW | `Std.Attribute` 基类 |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `GetCustomAttributes()` extern + `GetAttribute(Type)`（z42 实现）|
| `src/libraries/z42.core/src/Reflection/MethodInfo.z42` | MODIFY | 同上 |

### 测试 + 文档（docs 不上锁）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/tests/attributes/basic.z42` | NEW | golden：声明 + 应用 + 反射 |
| `src/libraries/z42.core/tests/attributes.z42` | NEW | `[Test]`：class/method attribute + 缓存一致 + GetAttribute |
| `src/compiler/z42.Tests/AttributeParseTests.cs` | NEW | 解析 + AST 挂载 |
| `src/compiler/z42.Tests/AttributeBindTests.cs` | NEW | typecheck 错误（非 attribute 类 / 无匹配 ctor / 非常量参数）|
| `docs/design/language/attributes.md` | NEW | 特性设计文档（用法 + 5 项改进 rationale + 实现原理）|
| `docs/design/runtime/zbc.md` | MODIFY | 格式 changelog（attribute section + version bump）|
| `docs/design/language/reflection.md` | MODIFY | attribute 反射段；C3 移出 Deferred |
| `docs/roadmap.md` | MODIFY | §15 + 0.3.4 行标 C3 完成 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 释放 compiler+runtime+stdlib |

**只读引用**：`BenchmarkDesugar.cs`（合成模式）、`reflection.rs::builtin_type_fields`（builtin 模式）、`.claude/rules/version-bumping.md`（bump checklist）、`Argument` record（Ast.cs:405）。

## Out of Scope / Deferred（记 attributes.md Deferred）

- **target 校验 / `AttributeUsage`**：MVP 任意位可标，不验证。
- **field / parameter 目标**：MVP 仅 class + method。
- **泛型 attribute 类** + **`GetAttribute<T>()` 泛型糖**：等 0.5.x 泛型方法实例化（MVP 用 `GetAttribute(typeof(T))`）。
- **数组参数 / `typeof` 之外的复杂常量**：MVP 限标量字面量 + enum 成员 + `typeof`。
- **CustomAttributeData 式裸参数检视**（不实例化读原始 ctor 参数）：factory-thunk 模型只给活实例；裸参数视图推后。
- **attribute 继承**（Inherited）：不做。
- **init-only 强制不可变**：MVP 用约定 + 缓存；强制等 init-only 支持。

## Open Questions

- 无（设计 + scope + 改进点已与 User 对齐；6.5 gate 确认后实施）。
