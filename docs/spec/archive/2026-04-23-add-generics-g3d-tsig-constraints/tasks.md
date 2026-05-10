# Tasks: L3-G3d — 跨 zpkg 约束消费（TSIG 扩展）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** TSIG section 增加 `TypeParamConstraints` per class/func；消费方 TypeChecker
在 `new ImportedGeneric<T>()` / 泛型函数调用点做约束校验，不再依赖 VM loader 运行时兜底。
**原因：** G4g 后 stdlib List/Dictionary 带 `where T: IEquatable<T> + IComparable<T>`，
消费方看不到约束，错误 type arg 只在加载时（或更差：运行时失败时）才暴露。
**文档影响：** `docs/design/generics.md`（G3d 记录）、`docs/roadmap.md`（G3d ✅）、
`docs/design/ir.md`（如有 zbc 版本号变化 —— 本次只改 TSIG，zbc 不变）。

## 任务

- [x] 1.1 `ExportedTypes.cs`：新增 `ExportedTypeParamConstraint` record；
       `ExportedClassDef` / `ExportedFuncDef` 增加 `TypeParamConstraints` 可空字段
- [x] 1.2 `ExportedTypeExtractor`：从 `SemanticModel.ClassConstraints` / `FuncConstraints`
       提取 → `ExportedTypeParamConstraint` 列表
- [x] 1.3 `ZpkgWriter`：class/func section 附加约束块
       （u8 count，每条 { name, flags u8, iface_count u8, ifaces, base?, tpRef? }）
- [x] 1.4 `ZpkgReader`：镜像反序列化，forward-compat guard（保留 `ms.Position < ms.Length` 检查）
- [x] 1.5 `ImportedSymbolLoader`：返回原始 `ExportedTypeParamConstraint` 列表，
       延迟 rehydrate 到 TypeChecker（需要 `_symbols.Interfaces` 才能把 `IEquatable` 名字解析回类型）
- [x] 1.6 `TypeChecker`：新增 `MergeImportedConstraints` + `RehydrateConstraints`，
       与 Pass 0.5 的本地约束合并；本地定义同名 decl 时本地 win
- [x] 1.7 Unit test `TsigConstraintsTests`：3 个测试
       （zpkg binary round-trip、NonEq 拒绝、int 通过）全部通过
- [x] 1.8 GREEN：530 C# 测试 + 160 VM 测试全绿

## 备注

- 接口约束暂以名称匹配（不携带 TypeArgs）—— `where T: IEquatable<T>` 的 inner T
  通过 TypeChecker 的 type-param 上下文处理；跨 zpkg 只需要知道"T 必须实现 IEquatable"
- 如果需要携带 interface type args（跨参数链的完整校验），归入 G2.5 chain 迭代
