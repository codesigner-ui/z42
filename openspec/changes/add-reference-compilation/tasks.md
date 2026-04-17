# Tasks: 引用编译（Reference Compilation）

> 状态：�� 进行中 | 创建：2026-04-17

## 进度概览
- [ ] I1: TSIG 写入
- [ ] I2: TSIG 读取 + TypeChecker 注入
- [ ] I3: 清理 Unresolved 路径

## I1: TSIG 写入
- [ ] 1.1 新增 `z42.IR/ExportedTypes.cs` — ExportedModule 等 record 定义
- [ ] 1.2 `SectionTags.cs` 新增 TSIG tag
- [ ] 1.3 `ZpkgWriter` 新增 `BuildTsigSection()` — 从 SemanticModel/IrModule 提取类型签名序列化
- [ ] 1.4 `PackageCompiler` 传递 SemanticModel 到 zpkg 写入流程
- [ ] 1.5 重编译 stdlib，验证 zpkg 包含 TSIG section
- [ ] 1.6 dotnet build && dotnet test 全绿

## I2: TSIG 读取 + TypeChecker 注入
- [ ] 2.1 `ZpkgReader` 新增 `ReadTsigSection()` — 反序列化 ExportedModule
- [ ] 2.2 新增 `ImportedSymbolLoader` — ExportedModule ��� Z42ClassType/Z42FuncType 重建
- [ ] 2.3 `SymbolCollector.Collect()` 扩展 — 接受 imported symbols，合并到本地符号表
- [ ] 2.4 `TypeEnv` 移除 `BuiltinClasses` 硬编码
- [ ] 2.5 `PipelineCore` 扩展 — Compile/CheckAndGenerate 接受 imported symbols
- [ ] 2.6 `PackageCompiler` 扩展 — 加载 TSIG → 构建 imported SymbolTable → 传入编译流程
- [ ] 2.7 dotnet build && dotnet test && ./scripts/test-vm.sh 全绿

## I3: 清理 Unresolved 路径
- [ ] 3.1 `FunctionEmitterCalls.EmitUnresolvedCall` 简化 — 已解析的 stdlib 调用不再走此路径
- [ ] 3.2 新增引用编译测试（错误 stdlib 调用报错、正确调用类型检查通过）
- [ ] 3.3 docs/design/ 文档更新（zpkg TSIG section 格式）
- [ ] 3.4 dotnet build && cargo build && dotnet test && ./scripts/test-vm.sh 全绿

## 备注
