# Tasks: 架构评审改进

**变更说明：** 根据 docs/review.md 架构评审报告，分批实施 13 项改进
**原因：** 提升代码可维护性、安全性、可扩展性，为 Phase 2 语言特性铺路
**文档影响：** README.md（z42.IR、z42.Semantics 同步更新）

> 状态：🟢 已完成（第一批） | 创建：2026-04-17 | 完成：2026-04-17

## P0: 立即收益（S-M）

- [x] 1.1 ExprParser 优先级提取 `Precedence` 命名常量（ExprParser.cs）
- [x] 1.2 LanguageFeature 枚举化：string→enum、FeatureMetadata（Phase/依赖声明）、未知默认 false

## P1: 架构改善（S-M）

- [x] 2.1 IBoundExprVisitor / IBoundStmtVisitor 接口 + Accept 方法
- [x] 2.2 IrVerifier（Debug 构建自动运行：def-use、branch targets、exception table）
- [x] 2.3 细化 E0402 — 拆分 E0408 DuplicateDeclaration / E0409 VoidAssignment / E0410 InvalidBreakContinue / E0411 InvalidInheritance / E0412 InterfaceMismatch
- [x] 2.4 TokenDefs 关键字加 Phase 标注（KeywordDef record）

## P2: Phase 2 前置（M-L）

- [ ] 3.1 TypeChecker 职责分离（ISymbolBinder / ITypeInferrer / IFlowAnalyzer）→ 独立变更推进
- [x] 3.2 PassManager 框架（IIrPass + IrPassManager，初期 0 个 pass）
- [x] 3.3 静态初始化拓扑排序（Kahn 算法，FunctionEmitter.TopologicalSortStaticInits）
- [ ] 3.4 JsonDerivedType Source Generator → 独立变更推进
- [ ] 3.5 DiagnosticCatalog Source Generator → 独立变更推进

## P3: 长期投入（L）

- [ ] 4.1 错误恢复策略（Parser 同步点 + TypeChecker 函数级隔离）→ 独立变更推进
- [ ] 4.2 属性测试（Lexer/Parser round-trip）+ Fuzzing → 独立变更推进

## 验证

- [x] 5.1 dotnet build && cargo build — 无编译错误
- [x] 5.2 dotnet test — 396 passed
- [x] 5.3 ./scripts/test-vm.sh — 114 passed (interp + jit)
- [x] 5.4 文档同步 + README 更新

## 备注

- IrTypeMapping 重复已在前期 refactor 中解决，跳过
- _currentClass 已用 IDisposable ClassScope 修复，§3.2 聚焦于更大的职责分离
- P2-7（TypeChecker 职责分离）、P2-10/11（Source Generator）、P3 留作独立变更
