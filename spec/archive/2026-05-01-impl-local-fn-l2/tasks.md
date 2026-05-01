# Tasks: 实现 L2 Local Function (impl-local-fn-l2)

> 状态：🟢 已完成 | 创建：2026-05-01 | 归档：2026-05-01 | 类型：lang（实施变更）

## Scope 调整记录（实施阶段）

实施过程中两处与 spec 轻微偏离，归档前如实记录：

1. **`_lambdaOuterStack` 名字未重命名为 `_captureBoundaryStack`**：
   原 design Decision 4 提到要重命名以反映"扩展为 lambda + local fn 共用"。
   实施时发现重命名涉及现有 lambda 代码 6 处引用，纯命名调整未带行为变化，
   按"避免无意义代码搅动"原则保留旧名字。语义扩展属实（lambda + local fn
   共用同一栈），不影响正确性。
2. **真 bug 修复 — BindIdent 误判 shadowing 为 capture**：
   实施 golden test 时发现，`int Outer(int n) { int Inner(int n) => n*2; }`
   触发误报：内层 `n` 引用被判为捕获外层 `n`。根因是 BindIdent 仅检查
   `outerEnv.LookupVar` 是否非空，未确认变量是从外层链解析的还是被本地参数
   遮蔽。修复为新增 `TypeEnv.ResolvesVarBelowBoundary(name, boundary)` 助手，
   只有当 walk-up 链在到达 boundary 之前找不到才判为 capture。也同时修复了
   lambda 的同类 shadowing 场景（向后改进）。

## 进度概览
- [x] 阶段 1: AST + Parser
- [x] 阶段 2: TypeChecker（BindBlock 两阶段 + 分层 fn 表 + capture check）
- [x] 阶段 3: IR Codegen（lifting + call site 改写）
- [x] 阶段 4: 测试体系（Parser + TC + Golden + Examples）
- [x] 阶段 5: 文档同步 + GREEN + 归档

## 阶段 1: AST + Parser

- [x] 1.1 `Ast.cs`：新增 `LocalFunctionStmt(FunctionDecl Decl, Span Span) : Stmt`
- [x] 1.2 `StmtParser.cs`：新增 `IsLocalFunctionDecl(cursor)` lookahead
  - 处理两种起始：type-keyword（`int Helper(...)`）和 `(T)->R` 函数类型作返回类型
- [x] 1.3 `StmtParser.cs`：`ParseStmt` 在 `IsTypeAnnotatedVarDecl` 之前加 `IsLocalFunctionDecl` 路径
- [x] 1.4 `StmtParser.cs`：实现 `ParseLocalFunctionStmt` —— 复用 `TopLevelParser.ParseFunctionDecl` 的逻辑（如果可访问）或本地实现
- [x] 1.5 验证现有 var decl / `(T)->R` 变量声明仍正常

## 阶段 2: TypeChecker

- [x] 2.1 `TypeEnv.cs`：新增 `_localFuncs : Dictionary<string, Z42FuncType>`
- [x] 2.2 `TypeEnv.cs`：`DefineFunc(name, sig)` API
- [x] 2.3 `TypeEnv.cs`：`LookupFunc` 改为先查 scope chain 再 fallback root
- [x] 2.4 `TypeChecker.cs`：把 `_lambdaOuterStack` 重命名为 `_captureBoundaryStack`（语义扩展，仍向后兼容）
- [x] 2.5 `BoundStmt.cs`：新增 `BoundLocalFunction(string Name, IReadOnlyList<Z42Type> ParamTypes, Z42Type RetType, BoundBlock Body, Span Span) : BoundStmt`
  - 同时记录原 param 名（用于 Codegen 入参）
- [x] 2.6 `TypeChecker.Stmts.cs::BindBlock`：改造为两阶段
  - Pass 1：扫描所有 `LocalFunctionStmt`，DefineFunc 到 scope
  - Pass 2：BindStmt 逐条
- [x] 2.7 `TypeChecker.Stmts.cs`：实现 `BindLocalFunctionStmt`
  - 一层嵌套检查（`_captureBoundaryStack.Count > 0` → 报错）
  - 进入 body 前 Push parent env，body bind 完 Pop
  - 返回 BoundLocalFunction
- [x] 2.8 验证：递归 / 前向引用 / 重复声明 / Z0301 capture / 多层嵌套报错

## 阶段 3: IR Codegen

- [x] 3.1 `IEmitterContext.cs`：（可选）新增 `RegisterLocalFnLiftedName(orig, lifted)` 或在 FunctionEmitter 内本地维护
- [x] 3.2 `FunctionEmitter.cs`：增 `_localFnLiftedNames` 字典
- [x] 3.3 `FunctionEmitterStmts.cs`：`case BoundLocalFunction lfn` → 调用 `EmitLiftedFunction(<Owner>__<Name>, lfn)` + 注册 lifted name 映射
- [x] 3.4 `FunctionEmitterCalls.cs`：`EmitBoundCall.Free` 加分支查 `_localFnLiftedNames`，命中则用 lifted name
- [x] 3.5 实现 `FunctionEmitter.EmitLiftedFunction(name, BoundLocalFunction)` —— 复用 EmitFunction 模式
- [x] 3.6 验证：lifted IrFunction 命名 + 调用 emit 为静态 Call

## 阶段 4: 测试体系

### 4.1 Parser
- [x] 4.1.1 `LocalFunctionParserTests.cs` NEW
  - LF-1 AST 形状
  - LF-2 各种 local fn 形式（exp body / block body）
  - LF-2 与 var decl 消歧
  - LF-2 与 `(T)->R` 函数类型变量消歧

### 4.2 TypeChecker
- [x] 4.2.1 `LocalFunctionTypeCheckTests.cs` NEW
  - LF-3 前向引用 / 直接递归 / 重复声明
  - LF-4 可见性（外部不可见）+ shadow 顶层
  - LF-5 capture 拒绝（Z0301）
  - LF-6 嵌套深度限制

### 4.3 Golden tests
- [x] 4.3.1 `golden/run/local_fn_l2_basic/`：源码、`expected_output.txt`、`interp_only` 标记
- [x] 4.3.2 跑 `regen-golden-tests.sh` + `test-vm.sh`

### 4.4 Examples
- [x] 4.4.1 `examples/local_fn.z42` —— 直接递归 + 前向引用 + 多 local fn
- [x] 4.4.2 `examples/local_fn.z42.toml`

## 阶段 5: 文档同步 + GREEN + 归档

- [x] 5.1 `docs/roadmap.md`：L3-C 表 L2-C1b 标 ✅
- [x] 5.2 验证全绿：dotnet build + cargo build + dotnet test + test-vm.sh 全通过
- [x] 5.3 输出 verification report
- [x] 5.4 移到 `spec/archive/2026-05-01-impl-local-fn-l2/` + commit + push
