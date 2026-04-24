# Proposal: 引用编译（Reference Compilation）

## Why

当前 stdlib 引用依赖三个互不相关的 hack：
1. `TypeEnv.BuiltinClasses` — 20 个硬编码类名，跳过所有类型检查
2. `StdlibCallIndex` — 仅存函数名/参数个数，��类型信息
3. `FunctionEmitterCalls.EmitUnresolvedCall` — 160 行硬编码分发

结果：错误的 stdlib 调用不报错、返回值全是 Unknown、成员访问无类型信息。
新增 stdlib 类需要手动改 C# 源码。这不是一个可维护的编译器设计。

## What Changes

- zpkg 新增 TSIG（Type Signature）section，携带完整类型签名
- 编译 stdlib ���将 SemanticModel 中的类型信息序列化到 TSIG
- 编译用户代码时从依赖 zpkg 加载 TSIG，重建 Z42Type 并注入 TypeChecker
- 彻底移除 `TypeEnv.BuiltinClasses` 硬编码
- `using` 声明控制哪些命名空间的类型可见

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.IR/ExportedTypes.cs` | 新增 | 导出类型描述 record |
| `z42.IR/BinaryFormat/SectionTags.cs` | 修改 | 新增 TSIG tag |
| `z42.IR/BinaryFormat/ZpkgWriter.cs` | 修改 | BuildTsigSection |
| `z42.IR/BinaryFormat/ZpkgReader.cs` | 修改 | ReadTsigSection |
| `z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | 新增 | ExportedModule → Z42Type 重建 |
| `z42.Semantics/TypeCheck/SymbolCollector.cs` | 修改 | 接受 imported symbols |
| `z42.Semantics/TypeCheck/TypeEnv.cs` | 修改 | 移除 BuiltinClasses |
| `z42.Pipeline/PipelineCore.cs` | 修改 | 传递 imported symbols |
| `z42.Pipeline/PackageCompiler.cs` | 修改 | ���载 TSIG + 注入 |
| `z42.Semantics/Codegen/FunctionEmitterCalls.cs` | 修改 | 简化 Unresolved 路径 |

## Out of Scope

- ProjectManifest [dependencies] 解析（仍使用 libs/ 自动扫描）
- Rust VM 侧变更（TSIG 是编译器专用 section，VM 忽略）
- 跨包泛型（L3 特性）
