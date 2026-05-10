# Tasks: API 清洁度（批次 3）

> 状态：🟡 进行中 | 创建：2026-04-14

**变更说明：** L5 DiagnosticBag.PrintAll 职责拆分 + M5 StringPool 不可变化 + L2 _currentClass RAII
**原因：** 消除隐藏副作用与可变性暴露，提升代码可读性和稳健性
**文档影响：** 无（纯内部重构）

- [x] 1. 创建 openspec
- [ ] 2. L5: DiagnosticBag.PrintAll() → void，call sites 改用 HasErrors
- [ ] 3. M5: IrModule.StringPool → IReadOnlyList<string>
- [ ] 4. L2: TypeChecker._currentClass RAII scope
- [ ] 5. 验证全绿并提交
