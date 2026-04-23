# Tasks: INumber 迭代 1（Script-First 极简版）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增 stdlib `Std.INumber<T>` 接口 + primitive struct (int/long/float/double)
实现。**完全纯脚本**，符合 Script-First 原则：body 方法用 `+`/`-`/`*`/`/`/`%`
算子，自然编译成对应 IR 算术指令。零新 VM builtin、零 codegen 特化。

## 任务

### 阶段 1：TypeChecker `this` 类型修正 ✅
- [x] 1.1 `BindClassMethods` / `BindImplMethods`：primitive struct 方法的 `this`
      绑定为 `TypeRegistry.GetZ42Type(className)` 返回的 Z42PrimType
- [x] 1.2 附带修复：`CollectInterfaces` 激活接口 TypeParams，让 `T` 返回位置
      解析为 Z42GenericParamType（之前 IComparable/IEquatable 返回 int/bool 未触发）
- [x] 1.3 附带修复：`ExportedInterfaceDef` 新增 TypeParams 字段 +
      `RebuildInterfaceType` 根据 TypeParams 恢复 Z42GenericParamType（跨文件 TSIG）

### 阶段 2：stdlib INumber 接口 ✅
- [x] 2.1 新建 `z42.core/src/INumber.z42`（5 方法接口）
- [x] 2.2 struct int 加 `: INumber<int>` + 5 body 方法（`return this + other` 等）
- [x] 2.3 struct double 加 `: INumber<double>` + 5 body 方法
- [x] 2.4 新建 `Long.z42` `struct long : IComparable / IEquatable / INumber`
- [x] 2.5 新建 `Float.z42` `struct float : IComparable / IEquatable / INumber`
- [x] 2.6 rebuild stdlib

### 阶段 3：测试 ✅
- [x] 3.1 Golden test `87_generic_inumber` 端到端：
      `Double<T>`、`Product3<T>`、`DivMod<T>` 在 int / double 上工作
- [x] 3.2 interp + jit 双绿（164 → 166 VM 测试）

### 阶段 4：文档 + 归档 ✅
- [x] 4.1 `docs/design/generics.md` 新增 INumber 小节
- [x] 4.2 `docs/roadmap.md` L3-G2.5 数值约束标 ✅
- [x] 4.3 `z42.core/README.md` 列出新 struct + INumber 文件
- [x] 4.4 GREEN：561 编译器 + 166 VM (interp+jit) 全绿

## 关键设计决策

- **INumber 放 z42.core** 而非 z42.numerics：struct int 头部声明 `: INumber<int>`
  需要 INumber 在 z42.core 可见；反向依赖会循环。接口 10 行，不构成 core 体积问题
- **纯脚本 body** `return this + other` 走 IR AddInstr 等指令；**不**用 extern
  （遵守"非 core / 非 native 库的 stdlib 必须纯脚本"规则）；**不**做 codegen 特化
  （当前测量无性能需求；按 Script-First 等真正的测量数据触发）
- **不支持 bool / char 的 INumber**：数值语义不适用
- **未来规划**：
  - 若测量到性能问题，可加 IrGen 特化：primitive receiver + `op_Add` → 直接 AddInstr
  - operator 重载 / `a + b` 自动 desugar 到 `a.op_Add(b)` — 独立迭代

## 附带的 3 个修复（副产品）

1. **`this` primitive struct 类型**：primitive struct 方法体内 `this` 从
   Z42ClassType 改为 Z42PrimType（通过 `TypeRegistry.GetZ42Type`）
2. **接口方法返回位置的 T**：`CollectInterfaces` 激活接口 TypeParams
3. **TSIG TypeParams 传播**：`ExportedInterfaceDef` + `RebuildInterfaceType`
   在跨文件 / 跨 zpkg 时还原 T 为 Z42GenericParamType

三个修复都是 pre-existing 正确性问题，此次暴露并修正。IComparable/IEquatable
的返回位置没有 T（都是具体 int/bool），所以之前没触发。
