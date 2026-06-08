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

### 1A-4b（TypeChecker.Infer + SemanticModel + SemanticDump — ✅ 已完成）
- [x] `SemanticModel`（Symbols + Bodies StrMap，key="Class.Method"/func 名）
- [x] `TypeChecker.Infer`（集中 if-is `_bindExpr`/`_bindStmt`；绑定类方法[this+字段入 scope]/顶层 func；var 推断 / 显式类型 init 可赋检查 / return 类型检查 / 标识符·函数解析）
- [x] `SemanticDump.DumpBody(src,key)` / `ErrorCount(src)`（纯函数）
- [x] tests/typecheck/（7 单测：var 推断/显式类型+赋值/字段入 scope/自由函数调用 + 错误：return mismatch/undefined ident/undefined func）
- [x] 验证：`xtask test compiler-z42` → **12 units 119 cases** 全绿
- 延后：driver `--dump-bound`（小，需加 semantics 依赖）；AST 节点暂不携 Span（诊断用占位）

> **🎉 increment 1A 完成：z42c.semantics 端到端类型检查真实 z42 源跑通**（source → syntax parse → SymbolCollector + TypeChecker → Bound 树 + 诊断）。集中 if-is 调度（D1）验证可行。下一步 1B（运算 + 控制流）。

## increment 1B（二元/一元运算 + if/while/break/continue + 数值拓宽表）—— ✅ 已完成
> z42 无 Func 委托/泛型字典 → C# `BinaryTypeTable`（Func + Dictionary）用 **int tag + if-else 串**忠实镜像（同 record→class、match→if-is 受限写法）。
- [x] 1B-1 `BinaryTypeTable.z42`（NEW）：OperandKind/ResultKind（int tag 常量）+ TypeFacts（IsNumeric/IsIntegral/IsOrderable/IsBool/SatisfiesKind/ArithmeticResult，含 keyword+canonical 双拼写）+ BinaryRule（class 替代 record）+ BinaryTypeTable.Lookup/LookupUnary/ResultType
- [x] 1B-2 `Bound.z42`：+ BoundLitFloat / BoundBinary `(bin op L R :t)` / BoundUnary `(unary op X :t)` + BoundIf `(if C T [E])` / BoundWhile `(while C B)` / BoundBreak `(break)` / BoundContinue `(continue)`
- [x] 1B-3 `TypeChecker.z42`：_bindExpr 加 FloatLitExpr/BinaryExpr/UnaryExpr；_bindStmt 加 IfStmt/WhileStmt/BreakStmt/ContinueStmt；+ `_loopDepth` 字段（break/continue 越界检查）+ `_requireBool` + `_checkOperand`
- [x] 1B-4 tests：bound_tests（运算/控制流节点 Dump，+2）+ typecheck_tests（算术/拓宽/比较→bool/逻辑/位运算/一元/if/while/break-continue + 错误：operator 类型不符/非 bool 条件/break 越界，+9）
- [x] 1B-5 README + tasks 同步；`xtask test compiler-z42` = **12 units 130 cases** 全绿（bound 6 / typecheck 16）

> **🎉 increment 1B 完成**：二元/一元运算（拓宽表）+ if/while/break/continue。

## increment 1C（方法调用 + receiver + 继承）—— ✅ 已完成
- [x] 1C-1：BoundMember(obj.field/this.field 含继承) + `SymbolTable.IsSubclassOf`(base 链迭代) + `_isAssignable`(subclass→base 可赋；wire 进 return/var-decl/assign 检查) + `_findField`
- [x] 1C-2：BoundCall 扩展 Kind(free/instance/static)+Receiver + `_bindMemberCall`(裸类名→static / 否则 bind 值→instance) + `_findMethod`(base 链)
- [x] 11 单测（成员字段/继承字段/subclass 可赋/无关类不可赋/字段未找到 + this.m()/param.m()/Class.m() 静态/继承方法/无此方法）
- [x] 验证：`xtask test compiler-z42` → **12 units 140 cases** 全绿（typecheck 26）

## increment 1D（is/as/new + 数组）—— ✅ 已完成
- [x] 1D-1：BoundIsExpr(x is T→bool)/BoundCast(x as T→T)/BoundNew(new T(args)→T，ctor 解析后续)；`_isAssignable` 改 error/unknown 任一侧吸收
- [x] 1D-2：Z42ArrayType(T[] 不变)+BoundIndex(arr[i]→elem)；ResolveType 加 ArrayType/NullableType→inner
- [x] 8 单测（is/as 向下转/new 无参·带参·未知类型 + 数组索引/同型赋值/非数组错误）
- [x] 验证：`xtask test compiler-z42` → **12 units 148 cases** 全绿（typecheck 34）

> **类型检查器现覆盖大部分 L1**：lit/ident/assign/decl/return/block/free·instance·static call/binary·unary/if·while·break·continue/member·继承/is·as·new/数组索引。

## increment 1E（三目 + ??）+ driver --dump-bound —— ✅ 已完成
- [x] BoundConditional(三目) + ?? via _bindBinary + _commonType；lambda/插值串延后
- [x] `z42c --dump-bound` CLI（SemanticDump.DumpAll + driver 加 z42c.semantics 依赖）——经自举二进制类型检查真实源

## increment 2A-1（泛型类）—— ✅ 已完成
- [x] Z42GenericParamType + ResolveTypeP(型参感知) + SymbolCollector 用类 TypeParams + TypeEnv.ResolveType/WithClassGeneric
- [x] 4 单测（Box<T>/Pair<K,V>/型参赋值/不同型参错误）→ **12 units 156 cases** 全绿

## increment 2A-2（泛型方法 + 实例化）—— ✅ 已完成
- [x] 2A-2a 泛型方法自身型参（_mergeParams 合并类+方法型参；free func 携自身型参）
- [x] 2A-2b Z42InstantiatedType(Box<int> 不变性；ResolveTypeP 处理 NamedType.Args 递归)；成员替换延后
- [x] 6 单测（泛型方法/泛型类内方法/free func + 实例化参数·返回·不变性·嵌套）→ **12 units 162 cases** 全绿

## increment 2B（where 约束求解，可行子集）—— ✅ 已完成
> User 裁决"可行子集"（2026-06-09）：C# 8 类约束中，z42c 当前类型模型只能干净检查 base-class/class/struct/型参引用 + 互斥；interface/enum/new()/func 延后（缺对应类型/信息）。决策表见 [design.md](design.md) "2B 实施决策"段。
- [x] 2B-1 `GenericConstraint.z42`（NEW）：ConstraintBundle（单型参：HasBaseClass/RequiresClass/RequiresStruct/HasTypeParamRef）+ ConstraintSet（一类全型参 bundle，按声明序与 TypeArgs 索引对齐；IndexOf/AnyNonEmpty）
- [x] 2B-2 `ConstraintChecker.z42`（NEW，从 TypeChecker 抽出隔离）：Resolve（Pass 0.5，where→ConstraintSet 登记进 SymbolTable.ClassConstraints；声明期诊断 E0401 未知型参 / E0402 class·struct 互斥）+ Check（call-site，`new Box<int>()` 逐 bundle 校验，违反 E0402）；可行子集外约束静默跳过不误报
- [x] 2B-3 `SymbolTable.z42`：+ ClassConstraints StrMap + HasConstraints/GetConstraints
- [x] 2B-4 `TypeChecker.z42`：Infer 起首 `_constraints.Resolve` + `_bindNew` 遇 Z42InstantiatedType `_constraints.Check`（+ ConstraintChecker 字段）
- [x] 2B-5 8 单测（base-class ok/violation·dump / class ok·violation / struct ok·violation / 型参引用 ok·violation / class·struct 互斥 / 未知型参 / 无约束任意实参）
- [x] 验证：`xtask test compiler-z42` → **12 units 170 cases** 全绿（typecheck 56）
- 注：TypeChecker.z42 抽出约束逻辑后回到 490 行（< 500 硬限）；约束逻辑落 ConstraintChecker（168）+ GenericConstraint（60）

## 后续增量
- [ ] **codegen(Bound→IR,semantics 另一半,需先 map z42.IR 出设计)** → z42.IR + byte-identical emit + pipeline
- [ ] **syntax gap**（z42c.syntax 待补）：局部 var-decl 泛型类型 `Box<int> b=...` 的 `_isVarDeclStart` lookahead
- [ ] 延后：闭包 L3 / interface+static-abstract / **2B 子集：interface·enum·new()·func-type 约束** / operator 重载 / 命名参数 / 跨包 TSIG import / 数组创建语法 / lambda / 插值串

## 备注
- SemanticsSkeleton.z42 暂留（pipeline 仍引用）。
