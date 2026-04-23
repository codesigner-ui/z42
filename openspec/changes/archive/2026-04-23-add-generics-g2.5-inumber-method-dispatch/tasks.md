# Tasks: L3-G2.5 INumber 约束（迭代 1：方法派发）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增独立 stdlib `z42.numerics.INumber<T>` 接口 + primitive 桥接，
让 `where T: INumber<T>` 可以在泛型体内通过 `a.op_Add(b)` 等方法调用
执行算术操作；int/long/float/double 原生实现 INumber<T>。

**原因：** L3-G2.5 🔥 高优先级项。数值约束是泛型算法（Sum/向量/矩阵）
的基础。本迭代闭环显式方法调用路径；下一迭代（迭代 2）加 `a + b`
自动 desugar 到 `op_Add` 完成人体工学。

**Scope 限制**：
- 只做 `op_Add / op_Subtract / op_Multiply / op_Divide / op_Modulo` 5 个算术方法
- 只覆盖 int/long/float/double 主流 4 类
- 不含 `a + b` 到 `a.op_Add(b)` 的自动 desugar（归迭代 2）
- 不含 operator 关键字 + 用户类运算符重载（归 L3 后期）
- 比较 `<` / `<=` 等归 IComparable<T>

**关键设计决策**：
- INumber **不是新约束 kind**，就是普通接口约束，走 L3-G2 既有机制。与
  enum 约束（flag 关键字）本质不同。只做了 1 行 `PrimitiveImplementsInterface`
  表扩展 + VM builtin 路由（与 `.CompareTo()` 同机制）
- INumber 放在**独立 package `z42.numerics`** 而非 `z42.core`，按需引入
  减少核心库体积

## 任务

### 阶段 1：stdlib 接口定义
- [x] 1.1 新建独立 package `src/libraries/z42.numerics/`
      （`z42.numerics.z42.toml` + `README.md` + `src/INumber.z42`）
- [x] 1.2 `scripts/build-stdlib.sh` LIBS 数组加入 `z42.numerics`
- [x] 1.3 `./scripts/build-stdlib.sh` 产出 `z42.numerics.zpkg` (845 bytes)

### 阶段 2：C# 编译器 primitive 桥接
- [x] 2.1 `PrimitiveImplementsInterface` 数值类型新增 `INumber` 分支
      （1 行表扩展，复用 L3-G4b 机制）
- [x] 2.2 `PrimitiveSatisfies` 无需修改（TypeArgs self-referential 校验自动继承）

### 阶段 3：Rust VM builtin 路由
- [x] 3.1 `primitive_method_builtin` I64/F64 × 5 算术 = 10 条映射
- [x] 3.2 `corelib/convert.rs` 10 个 builtin 函数
      （int wrapping，double IEEE 754）
- [x] 3.3 `corelib/mod.rs` 注册 10 个新 builtin

### 阶段 4：测试
- [x] 4.1 `TypeCheckerTests` 5 个 INumberConstraint 用例
      （int ✅ / double ✅ / string ✘ / class-without-impl ✘ / 错类型参数 ✘）
- [x] 4.2 Golden test `85_generic_inumber_method` 端到端
      （int + long + double，5 种算术组合，interp + jit 双绿）

### 阶段 5：文档 + 归档
- [x] 5.1 `docs/design/generics.md` 新增 INumber 约束小节
      （接口形态 + 桥接原理 + 迭代 2 规划 + 前向兼容 operator 重载）
- [x] 5.2 `docs/roadmap.md` L3-G2.5 表：INumber 迭代 1 ✅，迭代 2 新行
- [x] 5.3 `src/libraries/README.md` 列表加 z42.numerics
- [x] 5.4 GREEN 全绿：554 编译器 + 164 VM (interp+jit)

## 备注

- 规范调整：原计划把 INumber.z42 放在 z42.core/src/，User 要求挪到独立
  `z42.numerics` package 避免给核心库加体积；调整后实施路径更干净
- INumber 的本质是 "普通接口 + primitive 桥接"，不是新约束机制。整个
  编译期只改了 1 行（`PrimitiveImplementsInterface` 表加 `or "INumber"`）
- VM builtin 命名保持 Rust 风格短名 `op_Add`（非 C# IL `op_Addition`）；
  未来 operator 关键字 desugar 到同名方法，无 rework
- 整数溢出：`wrapping_*` 与现有 AddInstr/SubInstr 一致
- 除零：整数 bail!（panic on 0 divisor）；浮点走 IEEE 754 Inf/NaN
