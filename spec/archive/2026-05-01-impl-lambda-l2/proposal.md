# Proposal: 实现 L2 阶段无捕获 lambda (impl-lambda-l2)

## Why

`add-closures` 已锁定语言设计（2026-05-01 归档），但 src/ 中无对应实现。
当前状态：
- Lexer `=>` token ✅、表达式短写 ✅、`Z42FuncType` 语义类型 ✅、grammar.peg ✅
- Parser lambda 字面量 ❌、函数类型 `(T)->R` ❌、TypeChecker `case LambdaExpr` ❌、IR/VM ❌

不做会怎样：
- closure.md 与代码割裂，规范虚悬
- 后续 `impl-closure-l3` 完整闭包无前置基础
- stdlib 的 Map/Filter/Reduce 高阶 API 实现卡住（依赖 lambda 字面量）

本变更**端到端实现 L2 阶段无捕获 lambda 子集**，使下面这段代码全栈可跑：

```z42
(int) -> int inc = x => x + 1;          // 无捕获 lambda + (T)->R 类型
int Square(int x) => x * x;              // 表达式短写（已工作，回归保证）
int Outer() {                            // local function
    int Helper(int x) => x * 2;
    return Helper(3);
}
```

L3 完整闭包（捕获 + 三档实现 + Send + 诊断）拆到独立的后续 `impl-closure-l3` 变更。

## What Changes

按 archived `add-closures/specs/closure/spec.md` 的 R1–R4 + R9 + R14 落地 L2 子集：

### 实现的 Requirement
- **R1 Lambda 字面量语法**：`x => expr` / `(x, y) => expr` / `() => expr` / `x => { stmt; }` / `(int x) => expr`
- **R2 函数类型 `(T) -> R`**：解析、类型检查、参与泛型实参
- **R3 表达式短写**：已工作，加回归测试覆盖 R3 全部 Scenario
- **R4 Local function**：grammar 已支持，加 L2 无捕获检查 + 测试
- **R9 单目标闭包（parser 错误用例）**：`f += x => ...` 编译错误
- **R14 L 阶段限定**：lambda body 引用外层 local 时编译错误（捕获是 L3）

### 不实现的（L3 范围）
- R5/R6 捕获语义、R7 循环变量绑定、R8 spawn move、R10 闭包可比较、R11 不可序列化、R12 三档实现、R13 `Ref<T>` 共享、`--warn-closure-alloc` 诊断

### Pipeline 改动
- **AST**：新增 `FuncType : TypeExpr`；改造 `LambdaExpr` 让 `Params` 携带类型信息
- **Parser**：`TypeParser` 加 func_type 分支；`ExprParser.Atoms` 加 lambda 分支
- **TypeChecker**：`case LambdaExpr` → 类型推断；新增"无捕获检查"pass
- **IR Codegen**：lambda 字面量 → `loadfn`（无捕获 = 函数引用）
- **VM Interp**：`loadfn` 指令解释执行
- **测试**：parser unit + typecheck unit + golden run

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `FuncType` 节点；改造 `LambdaExpr.Params` 为 `List<LambdaParam>` |
| `src/compiler/z42.Syntax/Parser/TypeParser.cs` | MODIFY | 加 func_type 解析（`(T1, T2) -> R`）|
| `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs` | MODIFY | primary_expr 加 lambda 分支（消歧括号 vs lambda）|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | `case LambdaExpr` 类型推断 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | 注册 lambda 无捕获检查 pass |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `Z42FuncType` 完善（如需）|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | lambda 字面量 → `loadfn` 指令生成 |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 新增 `LoadFn` opcode（如不存在）|
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | `LoadFn` 反序列化 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | `LoadFn` 序列化 |
| `src/runtime/src/interp/ops.rs` | MODIFY | `LoadFn` 操作定义 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | `LoadFn` 解释执行 |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | `LoadFn` dispatch（如需）|
| `src/compiler/z42.Tests/ParserTests/LambdaTests.cs` | NEW | R1 Parser 单元测试 |
| `src/compiler/z42.Tests/ParserTests/FuncTypeTests.cs` | NEW | R2 Parser 单元测试 |
| `src/compiler/z42.Tests/ParserTests/NestedFnTests.cs` | NEW | R4 Parser 单元测试 |
| `src/compiler/z42.Tests/TypeCheckerTests/LambdaTypeCheckTests.cs` | NEW | R1+R14 TypeCheck 单元测试 |
| `src/compiler/z42.Tests/IrGenTests.cs` | MODIFY | lambda IR 生成测试 |
| `src/runtime/tests/golden/run/lambda_l2/` | NEW | R1/R3/R4 端到端 golden tests |
| `examples/lambda.z42` | NEW | L2 lambda 示例文件 |
| `spec/changes/impl-lambda-l2/proposal.md` | NEW | 本提案 |
| `spec/changes/impl-lambda-l2/specs/lambda/spec.md` | NEW | 本变更 spec |
| `spec/changes/impl-lambda-l2/design.md` | NEW | 实现设计 |
| `spec/changes/impl-lambda-l2/tasks.md` | NEW | 任务清单 |

**只读引用**（理解上下文用）：
- `docs/design/closure.md` — 行为契约
- `spec/archive/2026-05-01-add-closures/specs/closure/spec.md` — Requirement R1-R14 原文
- `src/compiler/z42.Syntax/Parser/Combinators.cs` — Parser 组合子风格参考
- `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` — 类型推断风格参考
- `examples/generics.z42` — 已工作的表达式短写参考

## Out of Scope

- ❌ L3 完整闭包（捕获分析 / 三档实现 / Send 派生 / `Ref<T>` 共享 / `--warn-closure-alloc`）→ `impl-closure-l3`
- ❌ JIT 路径（LoadFn / CallIndirect）→ 测试通过 `interp_only` 跳过；JIT 实现并入 `impl-closure-l3`
- ❌ AOT 实现（先 interp 全绿）
- ❌ stdlib 高阶 API（Map/Filter/Reduce 等）→ 独立 stdlib 提案
- ❌ `Func<T,R>` → `(T)->R` 迁移（保留 `Func<T,R>` 作 C# 风泛型，与新 `(T)->R` 函数类型并存；examples/generics.z42 不动）
- ❌ Lambda 的 `==` / `serialize` 行为（属 L3 R10/R11）
- ❌ **Local function**（嵌套函数声明）→ 实施中发现需要新增 `LocalFunctionStmt` AST + `SymbolCollector` 跨函数体扫描 + 命名 mangling，与"无捕获 lambda"目标偏离，拆出 follow-up `impl-local-fn-l2`（user 在阶段 7 实施前批准）

## Open Questions

- [ ] `LoadFn` opcode 是新增还是复用现有 `LoadFunc` / `LoadFuncRef`（需 spec/design 阶段查 Opcodes.cs 实证）
- [ ] Lambda 内部生成的匿名函数命名规则（建议 `<containing>$lambda$<index>`，与 C# IL 生成对齐）
- [ ] 表达式短写 R3 已工作但缺测试覆盖——本变更仅做"回归覆盖"还是同时做"补完测试矩阵"？建议后者，把 R3 五个 Scenario 全测一遍
- [ ] L2 无捕获检查的错误码：复用现有错误码体系还是新增？建议查 error-codes.md 后定
- [ ] `Func<T,R>` 与 `(T)->R` 两种语法在类型系统中是否等价？建议**等价**——`Func<T,R>` 自动 desugar 为 `(T)->R`（避免类型分裂）
