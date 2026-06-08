# Design: z42c.semantics — 类型检查层（AST → Bound tree）

> 状态：DRAFT（待 User 审批）｜归属：port-z42c-semantics
> 前置：z42c.syntax 完整（AST：Expr/Stmt/Decl + TypeExpr）；z42c.core（Diagnostic/Span）就绪。
> 来源：会话内 Explore agent 全量 map C# `z42.Semantics/TypeCheck/` + `Bound/`。

## 范围

本设计 = z42c.semantics 的**类型检查**半：`SymbolCollector` → `TypeChecker` → `Bound tree` + `Z42Type` + `Symbol model` + `TypeEnv` + `SemanticModel`。
**不含 codegen（Bound → IR，IrGen）** —— 那是 z42c.semantics 的另一半，依赖 z42c.ir 的 IR 模型，**单独设计（后续）**。

- **输入**：z42c.syntax 的 `CompilationUnit` AST。
- **输出**：`SemanticModel`（每方法体 → BoundBlock；每 Expr → BoundExpr；类型全解析为 Z42Type）。
- C# 对应：`TypeChecker.Check(cu)` = `SymbolCollector.Collect` + `TypeChecker.Infer`。

---

## 三个关键架构决策

### 决策 1：调度机制（★ 6d 遗留的 Visitor 决策，现由 semantics 消费者定型）

- **C# 实情**：`TypeChecker.BindExprCore` 与 codegen `FunctionEmitter.EmitBoundExpr` 都用**集中式 `switch` 模式匹配**（一个方法内 `switch (expr) { case BinaryExpr b => ... }`）。另有 `BoundExprVisitor<T>` 抽象基类（abstract VisitX + 一处 Visit() switch）供 Bound 树 walker 复用，但绑定/发射本身是集中 switch。
- **z42 约束**（已验证）：无 match / 无 switch-on-type；**无 `abstract class`**（stdlib 仅用 `virtual`）；`is`/`as` 已落地。
- **决定：集中式 if-is 链**。每个调度点（`_bindExpr(Expr)` / `_bindStmt(Stmt)` / 未来 `_emitExpr(BoundExpr)`）写一个 `if (e is IntLitExpr x) {...} else if (e is BinaryExpr b) {...} ... else { throw ICE }` 链。
  - 理由：① **1:1 镜像 C# 集中式 switch** 结构（易对照）；② z42 无 abstract → 无法编译期强制 VisitX 穷尽，full Visitor 只能 `virtual + default-throw`，与 if-is 的 `else throw ICE` **等价的运行期兜底**，却多一大堆 boilerplate；③ `is`/`as` 已落地，下行转换可靠。
- **不取**：full Visitor（virtual Accept + VisitX）。在无 abstract 的 z42 里它不提供编译期穷尽性，净增样板。**若将来 z42 引入 abstract 且 Bound walker 增多，可回头加 Visitor 基类**（design doc 记此演进点）。

### 决策 2：符号表表示（受限写法：泛型字段不可用）

- **C# 实情**：`Z42ClassType.Fields: Dictionary<string, IFieldSymbol>`、`SymbolTable.Classes: Dictionary<string, Z42ClassType>` 等大量 `Dictionary<string, T>` 作字段。
- **z42 约束**（已验证）：**类字段不能带泛型参数**（`private Dictionary<string,X> f;` 的 `<...>` 被 bootstrap parser 丢弃；stdlib 无此用法）。
- **决定：非泛型 `SymbolMap`**（`string key → ISymbol value`，内部并行 `string[] keys + ISymbol[] vals + int count`，线性查找；热点后续可换 hash）。同理 `TypeMap`（string → Z42Type）。所有"集合字段"用 **typed array + count**（沿用 z42c.syntax 全程的模式）。
  - 取舍：线性查找 O(n) 对编译器符号表偏慢，但**正确性优先**；类规模通常小（单文件几十符号）。byte-identical 不涉及（symbol 表不入产物）。可在 1F 增量引入 hashed map（仍非泛型，value 固定 ISymbol/Z42Type）。

### 决策 3：Z42Type（语义类型）vs TypeExpr（语法类型）—— 两套独立

- `TypeExpr`（syntax，已有）= **语法**类型（`NamedType "List<int>"` 文本结构，无符号信息）。
- `Z42Type`（semantics，新建）= **解析后语义**类型（`Z42ClassType` 携 field/method 符号回指 + 基类 + 类型形参）。
- **桥**：`SymbolTable.ResolveType(TypeExpr) → Z42Type`（按名查符号表 + 递归解析泛型实参/数组/nullable）。
- 不复用 TypeExpr 当语义类型（C# 也分两套：syntax TypeExpr vs semantic Z42Type）。

---

## Bound tree（z42c 节点集）

基类（受限写法：class 继承 + virtual，**非 record**；每节点携解析后 Type）：

```
class BoundExpr  { virtual Z42Type Type();  virtual string Dump(); }
class BoundStmt  { virtual string Dump(); }
```

- 每 `BoundExpr` 子类构造时传入已解析 `Z42Type`（`Type()` 返回它）。
- `Dump()` 出 s-expression（类比 syntax；供 [Test] 断言 + SemanticDump 工具）。Dump 含类型注解，如 `(lit-int 5 :int)`、`(bin + (ident x :int) (lit-int 1 :int) :int)`。
- **起步最小集（Phase 1A）**：`BoundLitInt` / `BoundIdent` / `BoundAssign` / `BoundCall`(free, stub) / `BoundError` + `BoundVarDeclStmt` / `BoundReturn` / `BoundExprStmt` / `BoundBlock`。逐增量加（全 30 BoundExpr / 16 BoundStmt 见 agent map，按增量计划摊开）。

---

## Z42Type hierarchy（z42c）

```
class Z42Type {
    virtual string  Name();
    virtual bool    IsAssignableTo(Z42Type other);   // 赋值兼容（含数值拓宽 / 继承 / null）
    virtual string  Dump();
}
```

- **起步（1A）**：`Z42PrimType`（int/long/bool/double/string/void 等，Name 即原名）/ `Z42ClassType`（Name + Fields(SymbolMap) + Methods(SymbolMap) + StaticFields/Methods + HasBase + BaseClassName）/ `Z42VoidType` / `Z42ErrorType` / `Z42UnknownType`。
- **后续**：`Z42ArrayType`(Elem) / `Z42FuncType`(Params[]+Ret+RequiredCount) / `Z42InterfaceType` / `Z42GenericParamType` / `Z42InstantiatedType`(Def+TypeArgs) / `Z42OptionType`(Inner) / `Z42EnumType`。
- **相等性**：C# 决策——Z42ClassType 相等仅比 `Name`（避免 ClassType→Symbol→ClassType 循环）。z42c 沿用：类型相等比 Name 字符串。

---

## Symbol model（z42c）

```
class ISymbol      { string Name; int Kind; Span DeclSpan; string Visibility; }   // Kind tag 替代下行
class IMethodSymbol: ISymbol { Z42Type ContainingType; Z42FuncType Signature; string Mods; bool HasDecl; MethodDecl Decl; }
class IFieldSymbol : ISymbol { Z42Type ContainingType; Z42Type FieldType; bool IsStatic; bool HasDecl; FieldDecl Decl; }
```

- 受限写法：class 继承 + `int Kind` tag（method/field/...）；用 class 而非 interface（虚方法 + 下行更省）。
- **两阶段构造**（镜像 C#）：SymbolCollector 先建 stub（ContainingType 待填）→ 收集完 fixup 回填 ContainingType（解 self-reference / 前向引用）。这正是 philosophy「跨阶段降级用 fixup pass 升级回」的标准做法。

---

## Pass 结构

```
Pass 0  SymbolCollector.Collect(cu) → SymbolTable
        （收集 enum/interface/delegate/class[字段+方法签名]/func 符号；不绑定体）
Pass 1  TypeChecker.Infer(cu, symbols) → SemanticModel
        （逐 class 方法 / 顶层 func：建 TypeEnv scope → 绑定形参/字段/this → BindBlock(体) → 类型检查）
        每方法 try/catch 隔离（一个方法 ICE 不带垮全文件）
Pass 2  FlowAnalyzer（reachability + definite assignment）—— 起步简化（仅"非 void 须全路径 return"），定值分析后续
```

- **入口**：`TypeChecker.Check(cu)`（Collect + Infer）→ SemanticModel。
- **SemanticModel**：`boundBodies`(MethodDecl→BoundBlock) + `exprBindings`(Expr→BoundExpr) + 符号表。起步用 SymbolMap/并行数组存。

---

## Scope（TypeEnv）

```
class TypeEnv {
    TypeEnv _parent;          // null = root
    TypeMap _vars;            // 本层 locals/params（string → Z42Type）
    SymbolTable _symbols;     // 全局 funcs/classes（root 持有，子层共享引用）
    bool _hasClass; string _currentClass;
    Z42Type LookupVar(string name);   // 走 _parent 链
    void Define(string name, Z42Type t);
    TypeEnv PushScope();  TypeEnv WithClass(string cls);
}
```

- 解析序：local（walk 链）→ class field（若 _hasClass）→ class method → 全局 func → class name → imported → 报 undefined。

---

## 增量计划（每增量走 `xtask test compiler-z42`，Bound s-expr 断言 + 类型断言 + 错误用例）

| # | 内容 | 关键节点 |
|---|------|---------|
| **1A** | 最小：非泛型 class + int/string 字段 + 简单方法体（var-decl/return/赋值/字面量/标识符/free-call stub）→ bind + typecheck → SemanticModel | BoundLitInt/Ident/Assign/Call/Error + VarDecl/Return/ExprStmt/Block；Z42PrimType/ClassType/Void/Error；SymbolCollector(class+func)；TypeEnv；SymbolMap |
| **1B** | 二元/一元运算 + if/while/block 嵌套（数值拓宽表）| BoundBinary/Unary + BoundIf/While/Break/Continue；BinaryTypeTable |
| **1C** | 方法调用 + receiver + 虚/静/实例分派 + 继承查找 | BoundMember/Index/Call(kinds)；arity 重载（无 modifier）；IsSubclassOf |
| **1D** | cast / new / 数组 | BoundCast/New/ArrayCreate/ArrayLit；ctor 解析 |
| **1E** | 三目 / `??` / 插值串 / lambda(无捕获) | BoundConditional/NullCoalesce/InterpolatedStr/Lambda(L2) |
| **2A/2B** | 泛型：类型形参 → 约束 | Z42GenericParamType/InstantiatedType；where 校验 |
| **defer** | 闭包 L3 捕获 / interface + static-abstract / operator 重载 / 命名参数 / 跨包 import(TSIG) | — |

**SemanticDump 工具**（类比 DumpTool）：`SemanticDump.Check(src) → bound s-expr`，driver 加 `--dump-bound`（与 `--dump-ast` 对齐）。

---

## 测试策略

- 每增量：源 → BindCheck → `BoundBlock.Dump()` s-expr 断言（含类型注解）+ 解析类型断言 + **错误用例**（type mismatch / undefined ident / missing return）断言 DiagnosticBag。
- 起步在 z42c.semantics/tests/ 新 unit（typecheck / bound）。
- 移除 SemanticsSkeleton（pipeline 仍引用时保留）。

---

## Deferred / 不在本设计内

- **Codegen（Bound → IR / IrGen）**：z42c.semantics 的另一半，**单独 design**（依赖先 map z42c.ir 的 IrModule/IrFunction/IrBlock 模型）。
- **byte-identical**：semantics 不产二进制（symbol/bound 不入产物）；byte-identical 诉求在 codegen/emit（.zbc）+ project（.zpkg）。
- FlowAnalyzer 定值分析完整版 / ClosureEscapeAnalyzer（L3）/ 跨包 TSIG import / 反射元数据。

---

## 决策点（已裁决 2026-06-08）

- **D1（调度）= 集中式 if-is 链**。每调度点一个 `if (e is X){...} else if ... else throw ICE` 链，1:1 镜像 C# 集中 switch。z42 无 abstract → full Visitor 无编译期穷尽优势、净增样板，不取。**此即 6d 遗留的 Visitor 决策最终定型：z42c 不引入 Visitor 基类。**（将来若 z42 引入 abstract 且 Bound walker 增多，可回头评估加 Visitor。）
- **D2（符号表）= 起步即上非泛型 hashed map**。`SymbolMap`（string → ISymbol）+ `TypeMap`（string → Z42Type）：内部 bucket 数组（key 的 hash → 桶；桶内 key+value+链/开放寻址）。**非泛型**（value 类型固定，规避类字段泛型限制）；可一个 `HashMapObj`（value=Object + 下行）复用，或两个近同类。查找 O(1)，起步即用（User 裁决，免后续从线性迁移）。
- **D3（增量起点）= 1A 最小集**（非泛型 class + int/string 字段 + 简单方法体：var-decl/return/赋值/字面量/标识符/free-call stub）。

> **本会话 scope = 设计 only（与 User 约定）。** 设计 + 决策已就绪；增量 1A 实施留新会话（后端最易错、需新鲜上下文）。实施时先建 `port-z42c-semantics` tasks.md + 占 ACTIVE.md（z42c 锁已在序列内）。
