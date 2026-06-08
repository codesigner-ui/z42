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

### 1A-3（SymbolCollector Pass 0）
- [ ] `SymbolTable` + `SymbolCollector.Collect(cu)`（class[字段+方法签名] + func；两阶段 stub→fixup ContainingType）
- [ ] tests/collect/（解析一个 class → 符号表断言）

### 1A-4（Bound tree + TypeChecker.Infer + SemanticModel + SemanticDump）
- [ ] Bound 最小集（LitInt/Ident/Assign/Call-stub/Error + VarDecl/Return/ExprStmt/Block，virtual Type/Dump）
- [ ] `TypeEnv`（scope 链 + TypeMap）；`TypeChecker.Infer`（集中 if-is `_bindExpr`/`_bindStmt`）；`SemanticModel`
- [ ] `SemanticDump.Check(src)`（→ bound s-expr）+ driver `--dump-bound`
- [ ] tests/typecheck/（bound s-expr 断言 + 类型断言 + 错误用例：type mismatch / undefined / missing return）

## 后续增量（设计 design.md 增量表）
- [ ] 1B 运算+控制流 / 1C 调用+继承 / 1D cast·new·数组 / 1E 三目·插值·lambda / 2A·2B 泛型
- [ ] 延后：闭包 L3 / interface+static-abstract / operator 重载 / 命名参数 / 跨包 TSIG import

## 备注
- SemanticsSkeleton.z42 暂留（pipeline 仍引用）。
