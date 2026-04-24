# Tasks: L3-G2.5 chain class-side — Class.Interfaces 携带 TypeArgs

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 类声明的接口列表从 `List<string>`（仅名字）升级为 `List<TypeExpr>`（AST）/
`List<Z42InterfaceType>`（语义层，带 TypeArgs）。现在 `class MyCls: IEquatable<int>` 的
`<int>` 在整个 pipeline 保留；`where T: IEquatable<int>` 校验会对比实际 args 是否对齐。
**原因：** L3-G2.5 chain 的 class-侧收尾 —— 之前只做了 primitive + generic param
的 TypeArg 校验，class 仍按名字匹配（documented gap）。
**文档影响**：`docs/design/generics.md`（补充说明）、`docs/roadmap.md`（已在 chain 下记录）。

## 任务

- [x] 1.1 AST `ClassDecl.Interfaces` 从 `List<string>` 改为 `List<TypeExpr>`
- [x] 1.2 Parser 用 `TypeParser.Parse` 代替 `ParseQualifiedName + SkipGenericParams`
       —— base/iface 列表条目保留完整 TypeExpr
- [x] 1.3 `SymbolCollector._classInterfaces` 从 `Dictionary<string, HashSet<string>>`
       改为 `Dictionary<string, List<Z42InterfaceType>>`
- [x] 1.4 `SymbolCollector.ResolveType` 对 `GenericType`→`Z42InterfaceType` 路径
       携带 TypeArgs（之前直接丢失）
- [x] 1.5 `SymbolTable.ClassInterfaces` 类型同步
- [x] 1.6 新方法 `ImplementedInterfacesByName(class, ifaceName)` 供 arg-aware 检查
- [x] 1.7 `TypeSatisfiesInterface` class 分支调用 `ClassSatisfiesInterface`
       （walks base chain，比较 TypeArgs）；`Z42InstantiatedType` 用
       `BuildSubstitutionMap` 替换类型参数
- [x] 1.8 `TypeArgEquals` 辅助 —— class/prim/generic-param 按 name 比较（避免 record
       结构相等在 stub vs full 间错配）
- [x] 1.9 Unit tests：3 个新用例（match / mismatch / instantiated class）
- [x] 1.10 GREEN：541 C# + 160 VM 全绿

## 备注

- **TSIG 跨 zpkg 仍按名字序列化**：`ExportedClassDef.Interfaces` 保持 `List<string>`
  —— imported 类的 interface-args 未跟踪；arg-aware 校验仅对**本地**类 100% 生效。
  跨 zpkg 的 `class Foo: IEquatable<int>` 在消费方只剩名字。独立迭代扩展 TSIG。
- 6 个原 pass 测试（Generic_*Constraint_*）短暂失败：根因是 `Z42ClassType` 的
  record 结构相等把 stub（collection 第一 pass）vs full（第二 pass）当不同，修复通过
  新 `TypeArgEquals` 辅助函数 —— class 比 name 而非结构
