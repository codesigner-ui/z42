# Design: L2 Local Function 实施策略

## Architecture

```
源码 `int Helper(int x) => x*2;` 在 block 内
  ↓ [Parser] StmtParser.ParseStmt → IsLocalFunctionDecl 命中 → 解析为 FunctionDecl，
              包装为 LocalFunctionStmt
  ↓ [TypeChecker]
       BindBlock pass 1: 收集所有 LocalFunctionStmt，注入子 TypeEnv 的 local fn 表
       BindBlock pass 2: 逐条 BindStmt（local fn body 在自己的 scope 中绑定 + capture check）
  ↓ [BoundAst] BoundLocalFunction（含 owner 引用 + lifted name 占位）
  ↓ [IrGen] FunctionEmitterStmts case BoundLocalFunction → EmitLifted（复用 lambda 路径）→
              RegisterLiftedFunction(IrFn `<Owner>__<Name>`)
            call site `Helper(3)` → `BoundCall.Free` with CalleeName="Helper" →
              Codegen 检测 local fn 表 → emit `Call Outer__Helper`
  ↓ [VM] 已有 Call 指令，无新增
```

## Decisions

### Decision 1: AST 表示 — Wrapper vs 直接复用 FunctionDecl
**问题**：local fn 是否需要新 AST 类型？
**选项**：A 新增 `LocalFunctionStmt(FunctionDecl)`；B 让 FunctionDecl 同时继承 Stmt
**决定**：**A**。理由：FunctionDecl 是顶层声明，混入 Stmt 层级会让 ASTwriter / pattern match 复杂；wrapper 节点干净

### Decision 2: BindBlock 两阶段实现
**问题**：如何支持前向引用？
**决定**：
```
BindBlock(block, parent, retType):
    scope = parent.PushScope()
    // Pass 1: 收集 local fn 签名
    foreach stmt in block.Stmts:
        if stmt is LocalFunctionStmt lf:
            sig = ResolveType(lf.Decl.Params...) -> Z42FuncType(...)
            scope.DefineFunc(lf.Decl.Name, sig)   // 注入到 scope-local fn 表
    // Pass 2: 绑定 stmt
    foreach stmt: bound.Add(BindStmt(stmt, scope, retType))
    return BoundBlock(bound)
```

### Decision 3: TypeEnv 分层 fn 表
**问题**：`LookupFunc` 当前只查全局表。
**决定**：扩展 TypeEnv 加 `_localFuncs : Dictionary<string, Z42FuncType>` per-scope，
  `LookupFunc(name)` 先查当前 scope chain，再 fallback 到 root global table。

### Decision 4: L2 无捕获检查复用
**问题**：lambda 用 `_lambdaOuterStack` 检测捕获；local fn 也需要类似机制？
**决定**：**复用现有 `_lambdaOuterStack`**，重命名为 `_captureBoundaryStack`（语义中性）。
  进入 local fn body 前 push parent env；BindIdent 已检查 `Peek().LookupVar`，自动覆盖 local fn 路径。

### Decision 5: 嵌套深度限制
**问题**：L2 仅一层 local fn；如何强制？
**决定**：在 `BindLocalFunctionStmt` 内检测 `_captureBoundaryStack.Count > 0`（即已在 lambda / local fn 内），是则报错"L2 不支持多层嵌套"。

### Decision 6: lifted 命名规则
**决定**：
- 顶层函数 `Outer` 内 local fn `Helper` → `<Namespace>.Outer__Helper`
- 类方法 `Demo.Calc.Compute` 内 local fn `Inner` → `Demo.Calc.Compute__Inner`
- 命名空间与现有 `<Owner>__lambda_<N>` 一致（双下划线分隔），不冲突

### Decision 7: call site 改写
**问题**：在 Outer body 内 `Helper(3)` 怎么 emit？
**决定**：
- TypeChecker `BindCall` 已通过 `env.LookupFunc("Helper")` 找到 local fn signature → 产生 `BoundCall.Free` with `CalleeName="Helper"`
- `FunctionEmitterCalls.EmitBoundCall.Free` 加分支：若 CalleeName 在 local fn 表中（通过 IEmitterContext 暴露当前函数的 local fn map）→ emit `Call <Owner>__<Name>`，否则走原有路径
- 等价：让 BoundCall 携带 ResolvedFunctionName 避免 Codegen 二次 lookup —— 简化版

## Implementation Notes

### Parser: IsLocalFunctionDecl lookahead

```csharp
private static bool IsLocalFunctionDecl(TokenCursor cursor)
{
    // type 起始：要么 type-keyword，要么 `(` 函数类型
    if (TypeParser.IsTypeToken(cursor.Current.Kind))
    {
        // 扫过 type expression（含 generic / array / nullable）后是否 IDENT '('
        int i = SkipTypeTokens(cursor, 0);   // 复用现有 `IsTypeAnnotatedVarDecl` 的 type-skip 逻辑
        return cursor.Peek(i).Kind == TokenKind.Identifier
            && cursor.Peek(i + 1).Kind == TokenKind.LParen;
    }
    if (cursor.Current.Kind == TokenKind.LParen)
    {
        // (T) -> R 函数类型 + IDENT + (
        int i = SkipFuncType(cursor, 0);
        return cursor.Peek(i).Kind == TokenKind.Identifier
            && cursor.Peek(i + 1).Kind == TokenKind.LParen;
    }
    return false;
}
```

### TypeChecker: BindLocalFunctionStmt

```csharp
private BoundStmt BindLocalFunctionStmt(LocalFunctionStmt lf, TypeEnv env, Z42Type retType)
{
    // L2 一层嵌套检查
    if (_captureBoundaryStack.Count > 0)
    {
        _diags.Error(DiagnosticCodes.FeatureDisabled,
            "nested local function is not allowed in L2; please move to top-level",
            lf.Span);
    }

    var fnDecl = lf.Decl;
    var paramTypes = fnDecl.Params.Select(p => ResolveType(p.Type)).ToList();
    var retTypeResolved = ResolveType(fnDecl.ReturnType);
    var sig = new Z42FuncType(paramTypes, retTypeResolved);

    // env.DefineFunc 已在 pass 1 完成（前向引用），这里仅 bind body

    var bodyEnv = env.PushScope();
    for (int i = 0; i < fnDecl.Params.Count; i++)
        bodyEnv.Define(fnDecl.Params[i].Name, paramTypes[i]);

    _captureBoundaryStack.Push(env);
    BoundBlock boundBody;
    try
    {
        boundBody = BindBlock(fnDecl.Body, bodyEnv, retTypeResolved);
    }
    finally
    {
        _captureBoundaryStack.Pop();
    }

    return new BoundLocalFunction(fnDecl.Name, paramTypes, retTypeResolved, boundBody, lf.Span);
}
```

### Codegen: lifting

```csharp
case BoundLocalFunction lfn:
{
    // emit lifted IrFunction
    var liftedName = $"{_currentFnQualName}__{lfn.Name}";
    var lifted = new FunctionEmitter(_ctx).EmitLiftedFunction(liftedName, lfn);
    _ctx.RegisterLiftedFunction(lifted);
    // local fn 名字 → lifted name 映射保存到当前 emitter 的 _localFnLiftedNames
    _localFnLiftedNames[lfn.Name] = liftedName;
    break;
}
```

`EmitBoundCall.Free` 改写：
```csharp
if (call.CalleeName is { } name && _localFnLiftedNames.TryGetValue(name, out var liftedQual))
{
    Emit(new CallInstr(dst, liftedQual, argRegs));
    return dst;
}
// ... 原有路径
```

## Testing Strategy

### 单元测试矩阵

| Requirement | 测试类型 | 文件 |
|---|---|---|
| LF-1/LF-2 AST + Parser | Parser unit | `LocalFunctionParserTests.cs` |
| LF-3 前向引用 / 递归 / 重复 | TypeCheck unit + golden | `LocalFunctionTypeCheckTests.cs` |
| LF-4 可见性 | TypeCheck unit | 同上 |
| LF-5 L2 capture 拒绝 | TypeCheck unit + golden error | 同上 |
| LF-6 嵌套深度限制 | TypeCheck unit | 同上 |
| LF-7 IR lifting | Codegen snapshot | `IrGenTests.cs#LocalFunction` |
| LF-8 端到端 | golden run | `golden/run/local_fn_l2_basic/` |

### Golden 路径

```
src/runtime/tests/golden/run/local_fn_l2_basic/
├── source.z42         # forward ref + recursion + multi local fn
├── expected_output.txt
├── source.zbc / source.z42ir.json   # generated by regen-golden-tests.sh
└── interp_only        # JIT 同 lambda 暂跳过（lifted fn 无 LoadFn 但调用路径可能含 lambda）
```

### 验证命令（GREEN 标准）

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

## Risk & Open Items

| 风险 | 缓解 |
|------|------|
| Parser lookahead 混淆 var decl / func decl / func-type var decl | 三种 lookahead 共用 SkipTypeTokens helper；优先级 func-decl > func-type-var > var-decl |
| BindBlock 两阶段化破坏现有逻辑 | pass 1 仅写 fn 表，不动 var/expr；pass 2 与原逻辑等价 |
| Lifted 命名与现有 `__lambda_<N>` / `__static_init__` 冲突 | 双下划线 + 命名空间限定 + 唯一 owner 路径 → 实际不冲突 |
| 顶层 Helper 与 local Helper shadow | TypeEnv 分层 LookupFunc 自然处理；call site 改写优先 local |
| 一层嵌套检查的实施位置 | 通过 `_captureBoundaryStack.Count > 0` 检测；同一栈也覆盖 lambda 内嵌 fn 的场景 |
