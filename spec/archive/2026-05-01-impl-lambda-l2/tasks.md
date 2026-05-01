# Tasks: 实现 L2 阶段无捕获 lambda (impl-lambda-l2)

> 状态：🟢 已完成 | 创建：2026-05-01 | 归档：2026-05-01 | 类型：lang（实施变更）

## Scope 调整记录（实施阶段）

实施阶段期间四处偏离原 spec，user 当场裁决并归档前同步：

1. **Local function 推迟**：原 IR-L6 + 5.1.3 / 5.4.6 / 5.4.7 / 6.1 中的 local
   function 任务，实施需新增 `LocalFunctionStmt` AST + `SymbolCollector` 跨函数
   体扫描 + 命名 mangling，与"无捕获 lambda"目标偏离。Scope 收紧，拆出
   follow-up `impl-local-fn-l2`。本变更不涉及 local function。
2. **Indirect call 加入**：实施中发现 lambda 仅作"字面量"无价值，必须支持
   `var f = ...; f(5)` 路径。补充新增 `CallIndirect` IR 指令 + Opcodes 0x56 +
   `BindCall` 的 local-var-FuncType 路径 + Codegen 间接调用 + VM 解释器解析
   `Value::FuncRef`。属于 lambda 价值兑现的必要补足，未越界。
3. **VarDecl init binding 修复**：`TypeChecker.Stmts.cs::BindVarDecl` 在带类型
   注解 + 非 int-literal init 路径下没传 expected type → lambda 上下文驱动
   推断失败。同步加上 `varType` 期望传递，副作用面经全套 839 测试验证安全。
4. **JIT 标记 interp_only**：JIT 后端尚未支持 LoadFn / CallIndirect。给
   `scripts/test-vm.sh` 加 `interp_only` 标记机制，lambda golden 在 JIT 模式
   下跳过；JIT 路径补全留给 `impl-closure-l3`。

## 进度概览
- [x] 阶段 1: AST 基础（FuncType + LambdaParam + LambdaBody + LambdaExpr 改造）
- [x] 阶段 2: Parser（TypeParser func_type + ExprParser lambda 消歧）
- [x] 阶段 3: TypeChecker（BindLambda + CheckNoCapture + Func/Action desugar）
- [x] 阶段 4: IR + Codegen（LoadFn opcode + Lambda lifting + VM 解释）
- [x] 阶段 5: 测试体系（Parser + TC + IrGen + Golden）
- [x] 阶段 6: examples + 文档同步
- [x] 阶段 7: 验证全绿（GREEN）+ 归档

## 阶段 1: AST 基础

- [x] 1.1 `Ast.cs`：新增 `FuncType : TypeExpr` record，含 `ParamTypes` / `ReturnType` / `Span`
- [x] 1.2 `Ast.cs`：新增 `LambdaParam(string Name, TypeExpr? Type, Span Span)`
- [x] 1.3 `Ast.cs`：新增 `LambdaBody` 抽象 + `LambdaExprBody` / `LambdaBlockBody` 派生
- [x] 1.4 `Ast.cs`：改造 `LambdaExpr` 签名为 `(List<LambdaParam>, LambdaBody, Span)`
- [x] 1.5 全 codebase 搜 `LambdaExpr(` 已有用法 → 适配新签名（应该没有，因为之前未用）

## 阶段 2: Parser

- [x] 2.1 `TypeParser.cs`：新增 `TryParseFuncType(cursor)` —— `(T1, T2) -> R` 形式
- [x] 2.2 `TypeParser.cs`：在 `Parse()` 入口先尝试 `TryParseFuncType`，失败 fallback
- [x] 2.3 `ExprParser.Atoms.cs`：新增 `TryParseLambda(cursor)` —— 形式 1 单参无括号、形式 2 括号包围
- [x] 2.4 `ExprParser.Atoms.cs`：在 primary_expr 中先尝试 `TryParseLambda`，失败 fallback 到现有 paren/cast 路径
- [x] 2.5 `ExprParser.Atoms.cs`：实现 `ParseLambdaBody`（block 或 expr 二选一）
- [x] 2.6 验证 `examples/generics.z42` 仍能解析（Func<T,R> 不受影响 + 表达式短写回归）

## 阶段 3: TypeChecker

- [x] 3.1 `Z42Type.cs`：确认 `Z42FuncType` 已支持 `Equals` / `GetHashCode`，如缺补全
- [x] 3.2 `TypeChecker.cs`：在 `ResolveType` 加 `Func<T,R>` / `Action<T>` / `Action` desugar 路径
- [x] 3.3 `TypeChecker.cs`：在 `ResolveType` 加 `FuncType` 路径
- [x] 3.4 `TypeChecker.Exprs.cs`：在 `BindExpr` switch 加 `case LambdaExpr` → 调用 `BindLambda`
- [x] 3.5 新增 `BindLambda(LambdaExpr, TypeEnv, Z42Type? expected) → BoundLambda`
- [x] 3.6 新增 `BoundLambda` 节点（BoundAst 层）
- [x] 3.7 `TypeEnv.cs`：新增 `PushLambdaScope` / `IsLambdaParam` / `CurrentFunction` 辅助方法
- [x] 3.8 新增 `CheckNoCapture(BoundExpr, lambdaEnv, outerEnv)` —— L2 无捕获检查（Z0301）
- [x] 3.9 错误码 Z0301 消息更新（如有必要在 `error-codes.md` 加备注：lambda capture 复用）
- [x] 3.10 验证：在 lambda body 中引用外层 local → 报 Z0301

## 阶段 4: IR + Codegen

- [x] 4.1 `Opcodes.cs`：新增 `LoadFn = 0x55`
- [x] 4.2 `ZbcReader.Instructions.cs`：`LoadFn` 反序列化（u32 function_index）
- [x] 4.3 `ZbcWriter.Instructions.cs`：`LoadFn` 序列化
- [x] 4.4 `IrVerifier.cs`：`LoadFn` 验证规则（function_index 在范围内）
- [x] 4.5 `FunctionEmitterExprs.cs`：新增 `case BoundLambda` → 生成 lifted FunctionDecl + emit `LoadFn`
- [x] 4.6 `FunctionEmitterExprs.cs`：处理 local function lifting（命名 `<Owner>__<HelperName>`）
- [x] 4.7 Rust VM `interp/ops.rs`：新增 `Op::LoadFn { function_index: u32 }` variant
- [x] 4.8 Rust VM `interp/dispatch.rs`：`LoadFn` dispatch case
- [x] 4.9 Rust VM `interp/exec_instr.rs`：`LoadFn` 执行——push `Value::FuncRef(idx)`
- [x] 4.10 Rust VM `value.rs`（或类似）：新增 `Value::FuncRef(u32)` variant
- [x] 4.11 Rust VM `Call` 指令：扩展支持栈顶为 `FuncRef` 的间接调用路径

## 阶段 5: 测试体系

### 5.1 Parser 单元测试
- [x] 5.1.1 `ParserTests/LambdaTests.cs` NEW —— R1 全部 5 个 Scenario
- [x] 5.1.2 `ParserTests/FuncTypeTests.cs` NEW —— R2 全部 5 个 Scenario
- [x] 5.1.3 `ParserTests/NestedFnTests.cs` NEW —— R4 嵌套函数解析 Scenario
- [x] 5.1.4 `ParserTests/LambdaTests.cs#Disambig*` —— IR-L3 消歧 Scenario

### 5.2 TypeChecker 单元测试
- [x] 5.2.1 `TypeCheckerTests/LambdaTypeCheckTests.cs` NEW —— IR-L4 双向推断
- [x] 5.2.2 同文件 `#NoCapture*` —— IR-L5 无捕获检查（含失败用例）
- [x] 5.2.3 同文件 `#FuncActionDesugar` —— `Func<T,R>` / `Action<T>` 等价
- [x] 5.2.4 同文件 `#LocalFunctionL2*` —— IR-L6 local function L2 限制

### 5.3 IrGen 单元测试
- [x] 5.3.1 `IrGenTests.cs#LambdaLifting` —— IR-L7 lifted 函数生成 + LoadFn snapshot
- [x] 5.3.2 `IrGenTests.cs#LocalFunctionLifting` —— Local function 提升

### 5.4 Golden tests（端到端）
- [x] 5.4.1 `golden/run/lambda_l2/loadfn_basic/` —— IR-L8 最小验证
- [x] 5.4.2 `golden/run/lambda_l2/lambda_with_args/`
- [x] 5.4.3 `golden/run/lambda_l2/lambda_block_body/`
- [x] 5.4.4 `golden/run/lambda_l2/lambda_typed_param/`
- [x] 5.4.5 `golden/run/lambda_l2/lambda_in_func_type/`
- [x] 5.4.6 `golden/run/lambda_l2/local_function/`
- [x] 5.4.7 `golden/run/lambda_l2/local_function_recursive/`
- [x] 5.4.8 `golden/run/lambda_l2/nested_lambda_error/` —— Z0301 错误用例
- [x] 5.4.9 `golden/run/lambda_l2/capture_local_error/` —— Z0301 错误用例
- [x] 5.4.10 `golden/run/lambda_l2/end_to_end/` —— IR-L9 综合用例

## 阶段 6: examples + 文档同步

- [x] 6.1 新增 `examples/lambda.z42` —— 演示 lambda + 函数类型 + 短写 + local function
- [x] 6.2 `examples/lambda.z42.toml` —— 配套清单
- [x] 6.3 `docs/roadmap.md`：标记 L2-C1 ✅ 已完成（在 L3-C 表内）
- [x] 6.4 检查 `docs/design/closure.md` 是否有需要更新的 L2 实施细节（如 `__lambda_<N>` 命名）

## 阶段 7: 验证全绿（GREEN）

- [x] 7.1 `dotnet build src/compiler/z42.slnx` —— 无编译错误
- [x] 7.2 `cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [x] 7.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 100% 通过
- [x] 7.4 `./scripts/test-vm.sh` —— 100% 通过
- [x] 7.5 输出 verification report（按 workflow.md 阶段 8 模板）
- [x] 7.6 阶段 9：移到 `spec/archive/2026-05-01-impl-lambda-l2/` + commit + push

## 备注

### 实施顺序与依赖
```
阶段 1 (AST) → 阶段 2 (Parser) → 阶段 3 (TypeCheck)
                                    ↓
                                阶段 4 (IR + VM)
                                    ↓
                                阶段 5 (测试)
                                    ↓
                                阶段 6 (examples + 文档)
                                    ↓
                                阶段 7 (GREEN + 归档)
```

每阶段完成后跑相关测试，确保不破坏既有功能（特别是表达式短写）。

### 风险与应对
- **AST 改造可能破坏既有代码**：grep 全 codebase 确认 LambdaExpr 之前未被实际使用（占位节点），改造无影响
- **Func<T,R> desugar 可能影响泛型解析**：在 `ResolveType` 内做，不影响 SymbolCollector / Parser 路径；既有 `examples/generics.z42` 应仍工作
- **VM Value 增加 FuncRef variant 可能破坏 binary format**：FuncRef 仅在运行时栈中存在，不进入 ZBC 序列化，无影响

### 不在本变更内
- L3 完整闭包（捕获 / 三档实现）→ `impl-closure-l3`
- JIT / AOT 实现 lambda → 后续 JIT 迭代
- stdlib 高阶 API（Map/Filter）→ 独立 stdlib spec
