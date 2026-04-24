# Tasks: 架构审查中期改进（A5/A1/A7）

**变更说明：** 实施架构审查报告中的中期三项改进
**原因：** Parser 多错误报告（A5）、消除语义层分裂（A1）、静态类型调用解析（A7）
**文档影响：** docs/design/language-overview.md（错误恢复行为）

> 状态：🟢 已完成（A5） | 创建：2026-04-15 | 完成：2026-04-15

## 进度概览
- [x] A5: Parser 错误恢复（多错误报告）
- [ ] A1: 引入 SemanticModel（未启动）
- [ ] A7: IrGen 调用解析利用静态类型（未启动）

## A5: Parser 错误恢复
- [x] 5.1 AST: 添加 ErrorExpr / ErrorStmt 节点
- [x] 5.2 Parser: 添加 DiagnosticBag 属性
- [x] 5.3 TopLevelParser: 声明级错误恢复（skip to next decl）
      + ParseEnumDecl: 枚举成员修饰符错误内部处理
      + ParseClassDecl: 类成员解析错误内部 try/catch 恢复
- [x] 5.4 StmtParser: 语句级错误恢复（skip to next stmt）
      + 修复无限循环 bug：catch 块中先前进一个 token 再 SkipToNextStmt
      + ParseBlock：缺少 `}` 时 diags != null 时报告诊断并返回 Ok，不再抛出
- [x] 5.5 TypeChecker: 处理 ErrorExpr/ErrorStmt
- [x] 5.6 FunctionEmitter: 处理 ErrorExpr/ErrorStmt
- [x] 5.7 Pipeline: 使用 parser.Diagnostics 替代 catch ParseException
      (GoldenTests, PackageCompiler, SingleFileCompiler)
- [x] 5.8 测试: 多错误报告 golden test (errors/21_multi_error)
      + 更新 ParserTests 单元测试 API（Diagnostics 而非 throw）
- [x] 5.9 验证全绿：396/396 ✅

## 备注
- A1/A7 未在本次迭代启动，作为独立任务继续
