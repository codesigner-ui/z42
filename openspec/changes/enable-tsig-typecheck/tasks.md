# Tasks: 启用 TSIG 类型检查 + 按需加载

> 状态：🟡 进行中 | 创建：2026-04-18

**变更说明：** 按需加载依赖 TSIG、TypeChecker 真正使用 TSIG 做类型检查、清理 EmitUnresolvedCall 硬编码
**原因：** TSIG 基础设施已就位但未被实际使用，stdlib 调用完全没有类型检查
**文档影响：** 无（纯内部实现改进）

## A1: 按需加载 TSIG
- [ ] 1.1 `ScanLibsForNamespaces` 改为同时返回 namespace → zpkg 完整路径映射
- [ ] 1.2 新增 `TsigCache` — 按需加载 + 缓存 zpkg TSIG
- [ ] 1.3 `CompileFile` 改为按 using 声明按需加载，不传 allTsig
- [ ] 1.4 删除 `LoadAllTsig` 方法
- [ ] 1.5 GoldenTests 同步改为按需加载
- [ ] 1.6 验证全绿

## A2: TypeChecker 使用 TSIG 做类型检查
- [ ] 2.1 `MergeImported` 改为将导入类加入 `_classes`（不再只跟踪名字）
- [ ] 2.2 为导入类添加 namespace → qualified name 映射，IrGen 用正确的限定名
- [ ] 2.3 `BindCall` 对导入类：方法存在 → 正式解析；方法不存在 → 报错
- [ ] 2.4 导入类方法调用的参数类型暂不检查（等 B 阶段补齐 stdlib 重载后再开启）
- [ ] 2.5 验证全绿

## A3: 清理 EmitUnresolvedCall
- [ ] 3.1 已由 TypeChecker 解析的 stdlib 调用不再走 Unresolved 路径
- [ ] 3.2 验证全绿
