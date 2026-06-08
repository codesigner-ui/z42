# z42c.semantics

## 职责
镜像 C# [z42.Semantics](../../compiler/z42.Semantics/README.md) 的**类型检查半**：`SymbolCollector`（Pass 0 符号收集）→ `TypeChecker`（Pass 1 绑定 + 类型检查）→ `Bound` 树（每节点携解析后 `Z42Type`）。codegen（Bound→IR）是另一半，待 z42c.ir map 后单独设计。首个硬子系统，dogfood 缺口高发段。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/Z42Type.z42` | 语义类型层次（Prim/Class/Func/Void/Error/Unknown）+ 数值拓宽 IsAssignableTo |
| `src/BinaryTypeTable.z42` | 运算类型规则表：OperandKind/ResultKind（int tag 替代 Func 委托）+ TypeFacts 数值谓词 + BinaryRule + Lookup/LookupUnary/ResultType |
| `src/Symbol.z42` | 符号模型（MethodSymbol / FieldSymbol）+ Z42FuncType 签名 |
| `src/StrMap.z42` | 非泛型 hashed map（string→object，开放寻址）—— 规避类字段泛型限制 |
| `src/SymbolTable.z42` | 类名→Z42ClassType / 顶层函数表 + `ResolveType`（TypeExpr→Z42Type 桥） |
| `src/SymbolCollector.z42` | Pass 0：两阶段建类 stub → 填字段/方法签名 + 顶层 func |
| `src/Bound.z42` | Bound 树节点（lit/ident/assign/call/binary/unary + decl/return/expr/block/if/while/break/continue），virtual Dump 出含类型注解 s-expr |
| `src/TypeEnv.z42` | 词法 scope 链（Vars StrMap）+ 全局符号表引用 |
| `src/TypeChecker.z42` | Pass 1：集中 if-is 调度 `_bindExpr`/`_bindStmt`，绑定方法体 + 类型检查 |
| `src/SemanticModel.z42` | 类型检查产物：符号表 + 各方法/函数体 Bound 树（key="Class.Method"/func 名） |
| `src/SemanticDump.z42` | 纯函数工具：源 → bound s-expr / 诊断计数（[Test] + driver `--dump-bound`） |

## 入口点
`new TypeChecker(diags).Infer(cu, symbols)` → `SemanticModel`（先 `new SymbolCollector().Collect(cu)` 出 `SymbolTable`）。便捷封装见 `SemanticDump.DumpBody(src, key)` / `ErrorCount(src)`。

## 依赖关系
→ z42c.core（Diagnostic/Span/DiagnosticCodes）, z42c.syntax（AST：Expr/Stmt/Decl + TypeExpr）, z42c.ir（codegen 半，当前未消费）。stdlib 自动可用。

## 增量进度
1A（最小类型检查：class+字段+简单方法体）✅ / **1B（二元·一元运算 + if/while/break/continue + 数值拓宽表）✅** / 1C 调用+继承（待）/ 1D cast·new·数组 / 1E 三目·插值·lambda / 2A·2B 泛型。codegen（Bound→IR）单独 design。
