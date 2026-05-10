# Tasks: 在 TypeCheck 时解析标准库调用

> 状态：🟢 已完成 | 创建：2026-04-17 | 完成：2026-04-18

**变更说明：** 扩展 DepIndex 包含参数类型，注入到 TypeChecker，消除 BoundCallKind.Unresolved 和 EmitUnresolvedCall

**原因：** 现有 DepIndex 可以加载完整的 stdlib 元数据，在 TypeCheck 时就能完全解析，无需延迟到 IrGen

**文档影响：** 无额外文档变更（纯内部重构）

---

## 实施清单

- [x] 1.1 扩展 DepCallEntry 添加参数类型数组和返回类型
- [x] 1.2 修改 DependencyIndex.Build() 从 IrFunction 提取类型信息
- [x] 1.3 添加 IrType → Z42Type 转换函数
- [x] 2.1 修改 TypeChecker 构造函数，添加可选 DependencyIndex 参数
- [x] 2.2 修改所有 TypeChecker 创建站点
- [x] 3.1–3.5 修改 BindCall 逻辑，消除所有 Unresolved 路径
- [x] 4.1–4.4 清理 BoundCallKind.Unresolved 及相关代码
- [x] 5.1–5.4 验证全绿

## 关键 commits
- `962b261` refactor(typecheck+codegen): 完全消除 BoundCallKind.Unresolved — 编译时解析所有 stdlib 调用
