# Design: L2 无捕获 Lambda 实施策略

## Architecture

```
源码 lambda
  ↓ [Lexer]      `=>` token 已有，无改动
  ↓ [Parser]     新增 FuncType / LambdaParam，改造 LambdaExpr；
                 ExprParser primary 加 lambda 分支（LL(k) lookahead 消歧）；
                 TypeParser 加 func_type 分支
  ↓ [SymbolCollector] lambda 不参与 symbol 收集（lifting 是 codegen 阶段做）
  ↓ [TypeCheck]  case LambdaExpr → BindLambda（推断 + L2 capture check）
  ↓ [BoundAst]   BoundLambda 节点，含完整类型信息和提升后函数 ID 占位
  ↓ [IrGen]      Lambda lifting：
                   - 收集 lambda 字面量 → 生成模块级独立 FunctionDecl
                   - 字面量位置生成 LoadFn 指令推入函数引用
  ↓ [VM]         Call 指令在栈顶函数引用值上调用（与现有 Call 路径同源）
```

## Decisions

### Decision 1: Lambda lifting 时机
**问题**：何时把 lambda body 提升为独立函数？
**选项**：
- A — Parser 阶段（生成 AST 时直接拆出 FunctionDecl）
- B — TypeCheck 后（BoundAST 阶段拆）
- C — IrGen 阶段（生成 IR 时拆）

**决定**：**C**（IrGen 阶段）。
**理由**：保留 AST/BoundAST 的语法树原貌（lambda 字面量是表达式语义），让 lifting 是 IR-level 的转换。便于未来 L3 引入 closure（届时 lifting 路径会扩展为 `mkclos`，而不是 `LoadFn`）；现在 L2 只生成 `LoadFn`。

### Decision 2: Lifted 函数命名
**问题**：lambda 提升后的内部函数名？
**约束**：z42 IDENT 文法 `[a-zA-Z_@] [a-zA-Z0-9_]*` 不允许 `$`。
**决定**：
- 匿名 lambda 提升 → `<Owner>__lambda_<N>`（双下划线前缀，编译器生成符号惯例，类似 Python 内部约定）
- Local function 提升 → `<Owner>__<HelperName>`（保留原名）

例：
- `App.Main` 内第 0 个 lambda → `App.Main__lambda_0`
- `App.Outer` 内的 `Helper` local fn → `App.Outer__Helper`

`<index>` 在所属 method 内从 0 递增；不进入用户符号表（不可被显式引用，TypeChecker 拦截）。

### Decision 3: LambdaExpr AST 结构
**决定**：
```csharp
public sealed record LambdaParam(string Name, TypeExpr? Type, Span Span);

public abstract record LambdaBody(Span Span);
public sealed record LambdaExprBody(Expr Expr, Span Span)        : LambdaBody(Span);
public sealed record LambdaBlockBody(BlockStmt Block, Span Span) : LambdaBody(Span);

public sealed record LambdaExpr(
    List<LambdaParam> Params,
    LambdaBody Body,
    Span Span) : Expr(Span);
```
**理由**：类型化的 sum type 比 `object?` / `Either<Expr, BlockStmt>` 更清晰；与 z42 现有 `sealed record` AST 风格一致。

### Decision 4: Parser 消歧 `(...)` 括号 vs lambda
**问题**：`(x, y)` 可能是 paren / lambda 参数列表；`(int x)` 可能是带类型 lambda 参数 / cast。如何消歧？
**决定**：**LL(k) lookahead** —— 在 `(` 之后扫描到匹配的 `)` 之后，看下一个 token 是否为 `=>`。
- 是 `=>` → lambda
- 不是 → fall back 到现有的 paren / cast / tuple 解析

实现：在 `ExprParser.Atoms` 中加 helper `TryParseLambda(ref TokenCursor)` —— 失败不消耗游标。

### Decision 5: TypeCheck lambda 推断方向
**决定**：**双向**。
- 调用 `BindExpr(lambda, expectedType)` 时，如果 `expectedType` 是 `Z42FuncType(P, R)`：
  - 推断每个 lambda 参数类型为对应 P[i]（如果用户没显式标）
  - body 类型按 R 期望推断；不匹配 → 错误
- 如果无 `expectedType`：
  - 所有参数必须显式标类型，否则 Z0402 "无法推断 lambda 参数类型"
  - body 类型自下而上推断
  - 整体类型 `Z42FuncType(标注的P, 推断的R)`

### Decision 6: L2 无捕获检查 — pass 位置
**决定**：**TypeCheck 内做**（在 `BindLambda` 完成后，紧接一个 `CheckNoCapture(boundLambda, currentScope)` 调用）。
**理由**：避免重新遍历 BoundAST；scope chain 在 `TypeEnv` 里现成。
**算法**：
```
CheckNoCapture(lambda, env):
    foreach IdentExpr / MemberExpr in lambda.Body:
        resolve symbol against env
        if symbol is a local var / param of an enclosing scope (not the lambda's own params, not a global / static):
            error Z0301: "lambda capture is L3 feature, not enabled in L2"
```

### Decision 7: 引用类型方法 / 字段判定（this 隐式）
**决定**：**算 L3 捕获**——L2 阶段直接报 Z0301。
**理由**：与 closure.md §4.2 / §5 的"this 捕获"设计一致；L2 提供"语法骨架"不含 this 隐式捕获能力。

**例外**：`static` 静态方法 / 静态字段 —— 不算捕获（全局符号）。

### Decision 8: LoadFn 操作码
**调研结果**：现有 Opcodes 中 0x50–0x5F 是 Calls 段，0x55–0x5F 空闲，**没有等价指令**。
**决定**：**新增 `LoadFn = 0x55`**（在 Calls 段紧邻 `Call` 系列）。
```
LoadFn <function_index: u32>      # 推入函数引用值（u32 = 模块内函数索引）
```
- Type tag 字段使用 `Object`（函数引用是 object kind）
- VM 内函数引用值 = `Value::FuncRef(u32)`（新增 Value variant）

### Decision 9: Func<T,R> / Action<T> 与 (T)->R 等价
**决定**：**全部等价**，desugar 到 `Z42FuncType`。
- `Func<T1, ..., Tn, R>` → `Z42FuncType([T1, ..., Tn], R)`
- `Action<T1, ..., Tn>`  → `Z42FuncType([T1, ..., Tn], void)`
- `Action`（无参）       → `Z42FuncType([], void)`
- `(T1, ..., Tn) -> R`   → `Z42FuncType([T1, ..., Tn], R)`

等价性体现在 `Z42Type.Equals` 一视同仁。
**理由**：避免类型分裂，让 stdlib 渐进迁移；`examples/generics.z42` 的 `Func<T,U>` 用法不需要改写；`Action<T>` 同步处理保证完整覆盖 C# 风泛型委托。

### Decision 10: 错误码 Z0301 复用
**决定**：**复用 Z0301**（feature not enabled）。
**错误消息明确表达**："`lambda capture` is L3 feature, not enabled in L2. Lambda 字面量本身在 L2 已可用，但引用外层 local `\"{name}\"` 的捕获能力是 L3 特性。"
**理由**：避免错误码膨胀；Z0301 的语义已涵盖"用了未启用的子 feature"。如未来用户反馈需要更细的错误码区分（"lambda 不可用" vs "lambda 捕获不可用"），再独立增补 Z0302。

## Implementation Notes

### Parser：lambda 字面量解析

```csharp
// ExprParser.Atoms.cs 新增
private ParseResult<Expr> TryParseLambda(TokenCursor cursor) {
    // 形式 1：单参无括号 IDENT '=>'
    if (cursor.Peek() is TokenKind.Ident && cursor.Peek(1) is TokenKind.FatArrow) {
        var name = cursor.Read();
        cursor = cursor.Advance(2);   // consume IDENT + =>
        var body = ParseLambdaBody(ref cursor);
        return Ok(new LambdaExpr([new LambdaParam(name, null, ...)], body, span));
    }

    // 形式 2：括号包围
    if (cursor.Peek() is TokenKind.LParen) {
        var savedCursor = cursor;
        if (TryParseParenLambdaParams(ref cursor, out var paramList)
            && cursor.Peek() is TokenKind.FatArrow) {
            cursor = cursor.Advance();   // consume =>
            var body = ParseLambdaBody(ref cursor);
            return Ok(new LambdaExpr(paramList, body, span));
        }
        cursor = savedCursor;   // backtrack
    }

    return Fail();   // 不是 lambda，让上层 fallback
}

private ParseResult<LambdaBody> ParseLambdaBody(ref TokenCursor cursor) {
    if (cursor.Peek() is TokenKind.LBrace) {
        var block = StmtParser.ParseBlock(ref cursor);
        return Ok(new LambdaBlockBody(block, span));
    }
    var expr = ExprParser.ParseExpr(ref cursor);
    return Ok(new LambdaExprBody(expr, span));
}
```

集成到 `primary_expr` switch：先尝试 `TryParseLambda`，失败再走原有 paren/cast/tuple 路径。

### Parser：函数类型解析

```csharp
// TypeParser.cs 新增
private ParseResult<TypeExpr> TryParseFuncType(TokenCursor cursor) {
    if (cursor.Peek() is not TokenKind.LParen) return Fail();
    var saved = cursor;
    cursor = cursor.Advance();

    var paramTypes = new List<TypeExpr>();
    if (cursor.Peek() is not TokenKind.RParen) {
        paramTypes.Add(ParseType(ref cursor));
        while (cursor.Peek() is TokenKind.Comma) {
            cursor = cursor.Advance();
            paramTypes.Add(ParseType(ref cursor));
        }
    }
    Expect(ref cursor, TokenKind.RParen);
    if (cursor.Peek() is not TokenKind.Arrow) {
        cursor = saved; return Fail();   // 不是函数类型
    }
    cursor = cursor.Advance();
    var ret = ParseType(ref cursor);
    return Ok(new FuncType(paramTypes, ret, span));
}
```

集成到 `Parse()`：先尝试 `TryParseFuncType`，失败再走原有类型路径。

### TypeChecker：BindLambda 算法

```csharp
private BoundLambda BindLambda(LambdaExpr lambda, TypeEnv env, Z42Type? expected) {
    Z42FuncType? expectedFn = expected as Z42FuncType;
    var paramTypes = new List<Z42Type>();
    var paramNames = new List<string>();
    for (int i = 0; i < lambda.Params.Count; i++) {
        var p = lambda.Params[i];
        Z42Type ptype = p.Type != null
            ? ResolveType(p.Type, env)
            : (expectedFn?.Params[i]
                ?? throw Z0402("无法推断 lambda 参数类型 " + p.Name));
        paramTypes.Add(ptype);
        paramNames.Add(p.Name);
    }

    var lambdaEnv = env.PushLambdaScope(paramNames, paramTypes);

    BoundExpr boundBody = lambda.Body switch {
        LambdaExprBody eb => BindExpr(eb.Expr, lambdaEnv, expectedFn?.ReturnType),
        LambdaBlockBody bb => BindBlockAsLambdaBody(bb.Block, lambdaEnv, expectedFn?.ReturnType),
    };

    CheckNoCapture(boundBody, lambdaEnv, env);

    var fnType = new Z42FuncType(paramTypes, boundBody.Type);
    return new BoundLambda(paramNames, paramTypes, boundBody, fnType, lambda.Span);
}
```

### CheckNoCapture 算法

```csharp
private void CheckNoCapture(BoundExpr body, TypeEnv lambdaEnv, TypeEnv outerEnv) {
    foreach (var id in body.CollectIdentRefs()) {
        var sym = id.ResolvedSymbol;
        if (sym is null) continue;   // 应该在 BindExpr 时已报错

        if (lambdaEnv.IsLambdaParam(sym)) continue;
        if (sym is StaticSym) continue;
        if (sym is FunctionDecl fn && fn == lambdaEnv.CurrentFunction) continue;  // 自身递归

        Error(Z0301, $"lambda capture of \"{id.Name}\" is L3 feature, not enabled in L2", id.Span);
    }
}
```

### IrGen：lambda lifting

```csharp
// FunctionEmitterExprs.cs 内
private void EmitLambda(BoundLambda lambda) {
    var liftedName = $"{currentFn.QualifiedName}__lambda_{lambdaCounter++}";
    var liftedFn = new FunctionDecl(
        Name: liftedName,
        Params: lambda.ParamNames.Zip(lambda.ParamTypes, (n, t) => new ParamInfo(n, t)),
        ReturnType: lambda.Type.ReturnType,
        Body: EmitLambdaBody(lambda.Body)
    );
    module.AddFunction(liftedFn);

    var fnIndex = module.GetFunctionIndex(liftedName);
    Emit(Opcodes.LoadFn, fnIndex);
}
```

Local function lifting 同理：`{Owner}__{HelperName}`（保留原名）。

### VM Interp：LoadFn 解释

`src/runtime/src/interp/exec_instr.rs`：
```rust
Opcode::LoadFn => {
    let fn_index = read_u32(&mut pc);
    let fn_ref = Value::FuncRef(fn_index);
    stack.push(fn_ref);
}
```

`Value::FuncRef(u32)` 是新增的 Value variant。`Call` 指令需要支持栈顶为 FuncRef 时的间接调用——或者沿用现有 indirect-call 机制（VCall 模式参考）。

### Func<T,R> / Action<T> / (T)->R 等价性桥接

`TypeChecker.cs` 中 `ResolveType(typeExpr)` 路径：
```csharp
if (typeExpr is GenericType g && g.Name == "Func") {
    var paramTypes = g.TypeArgs.Take(g.TypeArgs.Count - 1).Select(ResolveType).ToList();
    var retType = ResolveType(g.TypeArgs.Last());
    return new Z42FuncType(paramTypes, retType);
}
if (typeExpr is GenericType g2 && g2.Name == "Action") {
    var paramTypes = g2.TypeArgs.Select(ResolveType).ToList();
    return new Z42FuncType(paramTypes, Z42Type.Void);
}
if (typeExpr is NamedType n && n.Name == "Action" && /* no type args */) {
    return new Z42FuncType([], Z42Type.Void);   // Action 无参形式
}
if (typeExpr is FuncType f) {
    return new Z42FuncType(f.ParamTypes.Select(ResolveType).ToList(), ResolveType(f.ReturnType));
}
```

## Testing Strategy

### 单元测试矩阵（impl-spec Requirement → 测试位置）

| Requirement | 测试类型 | 文件 |
|---|---|---|
| IR-L1 LambdaExpr AST | Parser unit | `ParserTests/LambdaTests.cs` |
| IR-L2 FuncType AST | Parser unit | `ParserTests/FuncTypeTests.cs` |
| IR-L3 消歧 paren/lambda | Parser unit | `ParserTests/LambdaTests.cs#Disambig` |
| IR-L4 类型推断 | TypeCheck unit | `TypeCheckerTests/LambdaTypeCheckTests.cs` |
| IR-L5 无捕获检查 | TypeCheck unit | 同上 + 失败用例 |
| IR-L6 Local function L2 | TypeCheck unit | `ParserTests/NestedFnTests.cs` + TC tests |
| IR-L7 IR lifting | IrGen unit | `IrGenTests.cs#Lambda*` (snapshot) |
| IR-L8 VM LoadFn | VM golden run | `golden/run/lambda_l2/loadfn_basic/` |
| IR-L9 端到端 | golden run | `golden/run/lambda_l2/end_to_end/` |
| 表达式短写回归（archived R3）| golden run | 已有 `golden/run/53_expression_body/` 保留 + 新增覆盖 |

### Golden test 文件结构

```
src/runtime/tests/golden/run/lambda_l2/
├── loadfn_basic/           # IR-L8 最小验证
│   ├── source.z42
│   └── expected_output.txt
├── lambda_with_args/       # 多参 lambda
├── lambda_block_body/      # block body
├── lambda_typed_param/     # 显式类型参数
├── lambda_in_func_type/    # (T)->R 字段类型
├── local_function/         # local function
├── local_function_recursive/
├── nested_lambda_error/    # 嵌套 lambda 报 Z0301（only-error 类型）
└── capture_local_error/    # 捕获外层 local 报 Z0301
```

### 验证命令（GREEN 标准）

```bash
# 1. 编译验证
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml

# 2. 编译器测试
dotnet test src/compiler/z42.Tests/z42.Tests.csproj

# 3. VM 测试
./scripts/test-vm.sh
```

所有测试 100% 通过才能 commit。

## Risk & Open Items

| 风险 | 缓解 |
|------|------|
| Parser lookahead 性能下降 | 限制 lookahead 深度（最多扫描到匹配的 `)`），且 `(` 开头是常见路径——不影响热路径 |
| BlockStmt 引用循环（StmtParser 调 ExprParser 调 StmtParser）| 现有架构已支持（`ParseBlock` 是 Combinator delegate），无新增循环 |
| Lambda 捕获判定误伤静态成员 | TypeChecker 已区分 StaticSym vs LocalSym；新增的 capture check 复用此区分 |
| `Func<T,R>` / `Action<T>` ↔ `(T)->R` 等价性引入歧义 | 仅在 ResolveType 时 desugar，BoundAst / IR 层只看 Z42FuncType，无歧义 |
| LoadFn opcode 编号冲突 | 0x55 在 Calls 段尾部空闲，已确认 |
| Lifted lambda 命名冲突（同名 lambda 索引）| 索引在所属 method 内单调递增；模块级用 `Owner.Method__lambda_<N>` 全限定 |
| `__lambda_<N>` 命名与用户标识符冲突 | 双下划线前缀 + 全限定包含 method 名 → 与用户 IDENT 冲突概率极低；TypeChecker 拦截用户显式引用 lifted name |
