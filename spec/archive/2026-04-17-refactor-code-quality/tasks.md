# Tasks: 代码设计与框架结构改进

> 状态：🟢 已完成 | 创建：2026-04-17

**变更说明：** 修复性能问题、解耦紧耦合模块、拆分超限文件
**原因：** 代码审查发现多个违反项目硬性规则的文件，以及架构层面的耦合与性能问题
**文档影响：** 无（纯内部重构，不改行为/机制/接口）

## 第 1 批：性能 + 解耦（Codegen 模块）
- [x] 1.1 修复 IrGen.Intern() O(n) → O(1)（添加 Dictionary 反向索引）
- [x] 1.2 提取 ClassRegistry 消除 IrGen 四个平行字典
- [x] 1.3 提取 IEmitterContext 接口解耦 FunctionEmitter 对 IrGen 内部字段的直接访问

## 第 2 批：文件拆分（超硬限制）
- [x] 2.1 拆分 ZbcWriter.cs (701→364) + ZbcWriter.Instructions.cs (320) + StringPool.cs (28)
- [x] 2.2 拆分 ZbcReader.cs (529→339) + ZbcReader.Instructions.cs (197)
- [x] 2.3 拆分 TopLevelParser.cs (673→450) + TopLevelParser.Helpers.cs (235)
- [x] 2.4 拆分 TypeChecker.Exprs.cs (509→351) + TypeChecker.Calls.cs (169)

## 第 3 批：验证
- [x] 3.1 dotnet build && cargo build — 0 errors, 0 warnings
- [x] 3.2 dotnet test — 442 passed
- [x] 3.3 ./scripts/test-vm.sh — 114 passed (57 interp + 57 jit)
- [x] 3.4 受影响目录 README.md 更新（无需：纯内部重构）
