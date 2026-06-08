# Tasks: port-z42c-semantics — z42c.semantics 类型检查层移植

> 状态：🟡 进行中 | 创建：2026-06-08 | 子系统锁：z42c（顺序续作）
> **变更说明：** 把 C# `z42.Semantics` 的类型检查半（SymbolCollector / TypeChecker / Bound / Z42Type / Symbol）用 z42 重写。
> **设计**：[design.md](design.md)（D1 集中 if-is / D2 非泛型 hashed map / D3 1A 起点，已裁决）。
> codegen（Bound→IR）是另一半，单独 design（需先 map z42c.ir）。

## increment 1A（最小类型检查：非泛型 class + int 字段 + 简单方法体）—— 拆 4 子增量
### 1A-1（Z42Type 语义类型 — ✅ 已完成）
- [x] `Z42Type.z42`：Z42Type 基类 + Z42PrimType（数值拓宽 IsAssignableTo）/ Z42ClassType（Name+base）/ Z42VoidType / Z42ErrorType（吸收）/ Z42UnknownType
- [x] tests/types/（3 单测：拓宽 / error 吸收 / class+void）
- [x] 验证：`xtask test compiler-z42` → **8 units 99 cases** 全绿

### 1A-2（非泛型 hashed map + Symbol 模型 — ✅ 已完成）
- [x] 1A-2a `StrMap.z42`（string→object 开放寻址 hash，native GetHashCode，2x 重散列；100 条+下行往返测试）
- [x] 1A-2b `Symbol.z42`（MethodSymbol/FieldSymbol 独立 sealed class——1A 无混合集合，ISymbol 基类延后）+ `Z42FuncType`（签名）+ Z42ClassType 加 Fields/Methods（StrMap）
- [x] 验证：`xtask test compiler-z42` → **9 units 104 cases** 全绿（map 3 + types 5）

### 1A-3（SymbolCollector Pass 0 — ✅ 已完成）
- [x] `SymbolTable`（Classes/Functions StrMap + `ResolveType` TypeExpr→Z42Type 桥：prim/void/class，Array/泛型→Unknown）
- [x] `SymbolCollector.Collect(cu)`（两阶段：A 建类 stub → B 填字段/方法签名 + 顶层 func；unwrap AttributedDecl；跳 ctor；`_hasWord` 解析 static/visibility）
- [x] tests/collect/（4 单测：类成员/方法签名+static/顶层 func/兄弟类字段类型解析）
- [x] 验证：`xtask test compiler-z42` → **10 units 108 cases** 全绿（首个 syntax→semantics 跨包处理）

### 1A-4a（Bound tree + TypeEnv — ✅ 已完成）
- [x] `Bound.z42`：BoundLitInt/Str/Bool/Null / Ident / Assign / Call(free stub) / Error + VarDecl/Return/ExprStmt/Block（virtual Type/Dump，s-expr 含 `:type` 注解）
- [x] `TypeEnv.z42`：scope 链（Vars StrMap）+ Root 工厂 + PushScope/WithClass + 迭代 LookupVar
- [x] tests/bound/（4 单测：bound dump / block+assign / scope 链 / WithClass）
- [x] 验证：`xtask test compiler-z42` → **11 units 112 cases** 全绿
- **🔴 调试发现 3 个 z42 约束**（写入 memory）：① 对刚 new 的 self-type 实例跨实例写字段 → E0402（用全字段构造器）；② 在自身方法内对 self-type 字段调自身方法（递归）→ E0402（用迭代+字段读）；③ `while(true)` 不满足 definite-return（用 loop-flag + 末尾 return）

### 1A-4b（TypeChecker.Infer + SemanticModel + SemanticDump — 后续）
- [ ] `TypeChecker.Infer`（集中 if-is `_bindExpr`/`_bindStmt`，绑定方法体 + 返回类型检查）；`SemanticModel`
- [ ] `SemanticDump.Check(src)`（→ bound s-expr）+ driver `--dump-bound`
- [ ] tests/typecheck/（bound s-expr 断言 + 类型断言 + 错误用例：type mismatch / undefined / missing return）

## 后续增量（设计 design.md 增量表）
- [ ] 1B 运算+控制流 / 1C 调用+继承 / 1D cast·new·数组 / 1E 三目·插值·lambda / 2A·2B 泛型
- [ ] 延后：闭包 L3 / interface+static-abstract / operator 重载 / 命名参数 / 跨包 TSIG import

## 备注
- SemanticsSkeleton.z42 暂留（pipeline 仍引用）。
