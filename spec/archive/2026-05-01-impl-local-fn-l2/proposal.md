# Proposal: 实现 L2 阶段 Local Function (impl-local-fn-l2)

## Why

`impl-lambda-l2`（2026-05-01 归档）实施时发现 local function（嵌套函数声明）
需要 `LocalFunctionStmt` AST + 两阶段 BindBlock + lifting 命名 mangling，
与"无捕获 lambda"的最小目标偏离，user 当场裁决拆出本变更。

不做会怎样：
- closure.md §3.4 + add-closures spec R4 仍未实施，规范虚悬
- 需要写 helper 函数的用户必须把它放到顶层，破坏封装
- L3 完整闭包（impl-closure-l3）依赖此前置：闭包升级路径要求嵌套 fn 已就位

本变更**端到端实现 L2 阶段 local function 子集**，使下面这段代码全栈可跑：

```z42
int Outer() {
    int Helper(int x) => x * 2;                       // 简单 local fn
    int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);  // 直接递归
    int CallFwd() => Helper(10);                      // 前向引用
    return Helper(3) + Fact(5) + CallFwd();
}
```

L3 阶段（捕获外层 local）拆到 `impl-closure-l3`。

## What Changes

按 archived `add-closures/specs/closure/spec.md` R4 的 L2 子集实施：

### 实现的 Requirement
- **R4 Scenario 1 局部函数基本定义**：`int Outer() { int Helper(int x) => x * 2; ... }`
- **R4 Scenario 2 直接递归**：local fn body 内引用自身名字合法
- **R4 Scenario 3 可见性局限**：从所属函数外引用 → 编译错 name not found
- **R4 Scenario 4 L2 不允许捕获外层**：引用外层 local → 编译错 Z0301（复用 lambda 错误码）
- **R4 Scenario 5（L3 升级路径）**：本变更不实现，留给 `impl-closure-l3`

### 新行为：前向引用
C# 风格 local function 允许在 body 顶部调用 body 底部声明的 local fn。
要求 `BindBlock` 改为两阶段：先扫所有 `LocalFunctionStmt` 注入符号表，
再绑定每条语句的表达式 / 函数体。

### Pipeline 改动
- **AST**：新增 `LocalFunctionStmt(FunctionDecl Decl, Span Span) : Stmt`
- **Parser**：`StmtParser.ParseStmt` 新增 lookahead 路径 `IsLocalFunctionDecl(cursor)`，
  命中则解析为完整 `FunctionDecl`，包装为 `LocalFunctionStmt`
- **TypeChecker**：
  - `BindBlock` 两阶段化：pass 1 注入 local fn 签名到子作用域，pass 2 绑定 body
  - `TypeEnv` 新增 `DefineFunc(name, sig)` / `LookupFunc` 分层支持（当前 `LookupFunc` 只看根表）
  - L2 无捕获检查：local fn body 引用外层 local → Z0301（复用 lambda 路径，新增 `_localFnOuterStack` 或合并到 `_captureBoundaryStack`）
  - `BindCall` 路径已支持"调用 local var 持 FuncType"——local fn 名字也走 `env.LookupFunc` 找到 → 走 `BoundCall` Free 静态调用
- **IR Codegen**：
  - 新增 `BoundLocalFunction` Bound 节点
  - `FunctionEmitterStmts` 加 `case BoundLocalFunction` → 调用 `_ctx.RegisterLiftedFunction` 提升为 module-level
  - 命名 `<Owner>__<HelperName>`（与 `__lambda_<N>` 同命名空间）
  - **call site 改写**：在所属函数内调用 `Helper(x)` → emit `Call <Owner>__Helper`（不是 CallIndirect）
- **VM**：无改动（已有 `Call` 指令足够）
- **测试**：parser unit + typecheck unit + golden run

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `LocalFunctionStmt` 节点 |
| `src/compiler/z42.Syntax/Parser/StmtParser.cs` | MODIFY | `ParseStmt` 加 `IsLocalFunctionDecl` 路径 |
| `src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs` | MODIFY | 分层 fn 表 + DefineFunc API |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs` | MODIFY | `BindBlock` 两阶段 + `BindLocalFunctionStmt` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | local fn 捕获边界栈 |
| `src/compiler/z42.Semantics/Bound/BoundStmt.cs` | MODIFY | 新增 `BoundLocalFunction` |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs` | MODIFY | 处理 BoundLocalFunction → 注册 lifted fn |
| `src/compiler/z42.Tests/LocalFunctionParserTests.cs` | NEW | Parser 单元测试 |
| `src/compiler/z42.Tests/LocalFunctionTypeCheckTests.cs` | NEW | TypeCheck 单元测试（含 Z0301 capture 拒绝）|
| `src/runtime/tests/golden/run/local_fn_l2_basic/` | NEW | 端到端 golden test |
| `examples/local_fn.z42` | NEW | L2 local fn 示例 |
| `examples/local_fn.z42.toml` | NEW | 配套清单 |
| `docs/roadmap.md` | MODIFY | L3-C 表 L2-C1b 标 ✅ |
| `spec/changes/impl-local-fn-l2/proposal.md` | NEW | 本提案 |
| `spec/changes/impl-local-fn-l2/specs/local-fn/spec.md` | NEW | 行为规范 |
| `spec/changes/impl-local-fn-l2/design.md` | NEW | 实现设计 |
| `spec/changes/impl-local-fn-l2/tasks.md` | NEW | 任务清单 |

**只读引用**：
- `docs/design/closure.md` §3.4 — 行为契约
- `spec/archive/2026-05-01-impl-lambda-l2/` — lambda 实施参考
- `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` — 顶层 fn 解析风格参考

## Out of Scope

- ❌ L3 捕获外层 local（属 `impl-closure-l3`）
- ❌ Local function 内嵌 local function（即三层嵌套）—— L2 仅支持一层嵌套，多层等 L3 闭包统一处理
- ❌ 同名 overloading（local fn 同名参数列表不同）—— 报"重复声明"，与变量同处理
- ❌ JIT 路径（与 lambda 同——lifted fn 含 LoadFn / 间接调用时走 interp）

## Open Questions

- [ ] 一层嵌套限制如何在 parser/checker 层强制？建议在 BindLocalFunctionStmt 内检测 body 是否含另一 LocalFunctionStmt，若有报"L2 不支持多层嵌套"
- [ ] 命名冲突优先级：local fn `Helper` vs 顶层 fn `Helper` —— local fn 应 shadow 顶层。但 lifted name `<Owner>__Helper` 与顶层 `Helper` 在 module 层是不同符号，无 IR-level 冲突
- [ ] 本变更前 `IsTypeAnnotatedVarDecl` 已扩展支持 `(T) -> R` 类型；现在再加 `Type IDENT (` lookahead，需保证三种 form 互不干扰
