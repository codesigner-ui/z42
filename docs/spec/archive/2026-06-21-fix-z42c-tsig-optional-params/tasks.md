# Tasks: fix-z42c-tsig-optional-params

**变更说明：** z42c 导出 TSIG 时把可选参数（`Param.Default != null`）当必填 →
`minArgCount = paramCount`；C# 驱动读 z42c-built zpkg 跨包调用省略默认实参时报
`E0402: expected N–N argument(s)`。修 `ExportedTypeExtractor` 计算 requiredCount。
**原因：** dogfood S3（z42c 接管生产 stdlib build）暴露——z42.core `MulticastAction.Invoke(T, bool=false)` 等带默认参数方法的 TSIG arity 错误，12 个 delegate/event/weak golden 编译失败。
**文档影响：** docs/design/compiler/self-hosting.md（受限写法/parity 记录）。

> 状态：🟢 已完成 | 完成：2026-06-21

- [x] 1.1 `ExportedTypeExtractor.z42`：加 `_requiredCount(Param[], pc)`（镜像 C# `SymbolCollector.BuildFuncSignature`：首个带 Default 的参数下标 = required count）
- [x] 1.2 `_fromSymbol`（方法）+ `_extractFunc`（自由函数）用 requiredCount 作 minArgCount（替 pc）
- [x] 1.3 验证：z42c rebuild → z42c build stdlib → `multicast_action_basic` 编译通过（无 E0402）+ 运行输出匹配；xtask golden regen 199/0（含原 12 failing）；**compiler-z42 byte-identical 7/7 + C# 1571/1571 不回归**
- [x] 1.4 docs/design/compiler/self-hosting.md 同步（optional-param TSIG parity + inherited-arity Deferred）

## 备注
- `_fromImportedMethod`（继承自其它包的方法）暂留 pc：z42c `Z42FuncType` 不携 MinArgCount，import 时丢失。若 full gate 暴露继承默认参数方法 re-export 的 case，再扩 Z42FuncType.MinArgCount（更大改）。当前 12 failing 均直接定义方法。
