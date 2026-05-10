# Design: Closure 设计实施策略

## Architecture

### 编译器层面

```
源码
  ↓ [Lexer]      新增 `=>` token
  ↓ [Parser]     新增 LambdaExpr / FnTypeExpr / 嵌套 FnDecl / ExprBodyFn
  ↓ [TypeCheck]  捕获分析 + 逃逸分析 + Send 派生
  ↓ [IR Codegen] mkclos / callclos / mkref / loadref / storeref
  ↓ [zbc emit]   二进制
  ↓ [VM]         interp / JIT / AOT
```

### 运行时（闭包对象表示）

```
档 A 栈：
  struct __anon_env_N { captures... }   // 栈帧上
  call __anon_fn_N(&env, args)           // 直调

档 B 单态化：
  无运行时对象——闭包字面量 inline 到泛型函数体

档 C 堆：
  struct Closure { env_ptr: *T, vtable_ptr: *VT }
  struct __anon_env_M { captures... }    // RC/GC 堆
  call vtable.invoke(env, args)          // 间接
```

## Decisions

### Decision 1: 单一统一闭包类型 vs 三 trait 模型
**问题：** Fn / FnMut / FnOnce 三档 vs 单一 `(T) -> R`？
**选项：** A 单一类型；B Rust 三档
**决定：** A。值类型快照规则消除 FnMut 主要必要性；Send 派生独立于 Fn 三分；易用性优先。代价：FnOnce 的"消费式回调"精确语义由 move-only 类型搭配解决。

### Decision 2: 值类型捕获语义
**问题：** C# hoist by-ref vs 快照？
**决定：** 快照。消除"循环变量晚绑定 + 幻读"两类陷阱；与值类型语义一致。代价：闭包内修改外部值类型需 `Ref<T>` / `Box<T>`。

### Decision 3: 引用类型捕获语义
**问题：** 变量槽（C#）vs 对象身份？
**决定：** 对象身份。与快照规则一致——绑定的是"创建时的状态"；外部重新指向变量槽是常见 bug 源。

### Decision 4: 函数类型语法
**问题：** `Func<T,R>` vs `(T) -> R`？
**决定：** `(T) -> R`。避开 `Func` 17 重载历史包袱。z42 函数声明仍是 C# 风（`R Name(T x)`），所以**类型位置与声明位置不对称**——这是已知可接受的取舍（TypeScript 同样如此：`function add(a, b): number` 声明 vs `(a, b) => number` 类型）。

### Decision 5: 实现策略选择算法
**决定：**
- 具体 `(T) -> R` 形参 + 不逃逸 → 档 A
- 泛型 `<F: (T) -> R>` 形参 + 单态 → 档 B
- 字段 / spawn / 返回 / 集合 → 档 C
- 局部 var 中转 → 流分析后归类
- 兜底（无法判定）→ 档 C

### Decision 6: `=>` 双重含义消歧
- Lambda：表达式位置 `<params> => <body>`
- 函数短写：声明位置（C# 7+ expression-bodied）`R Name(T x) => <expr>;`
位置不同 → 不冲突。z42 现有代码已大量使用此形式（参见 `examples/generics.z42`），无新引入歧义。

### Decision 7: 单目标 vs 多播
**决定：** 单目标。多播是 4 类陷阱源头（异常吞 / 返回值丢 / 退订失效 / 泄漏），事件用 `EventEmitter<T>`。

## Implementation Notes

### Lambda 解析
- 表达式位置；优先级低于赋值，高于 ?:
- 单参可省括号；多参 / 显式类型必须 `(...)`
- Body：表达式 或 block；block 必须显式 `return`
- AST：`LambdaExpr { params, body: Expr | Block }`

### 函数类型解析
- `(T1, T2) -> R` 中的 `(...)` 是参数列表，非元组
- 箭头存在时一定是函数类型，无歧义
- AST：`FnTypeExpr { params: List<TypeExpr>, ret: TypeExpr }`

### 函数表达式短写解析
- C# 7+ expression-bodied：`R Name(T x)` 之后跟 `=>` 而非 `{` → 短写
- AST 复用 `FnDecl`，body = `FnBody = Block | ExprBody`
- 必须 `;` 结尾，避免与下条声明歧义
- z42 现有 `examples/generics.z42` 已用此形式（如 `Option<T>.Map`），新增设计与既有代码兼容

### 嵌套函数声明（local function）
- 词法作用域：仅在所属 block 内可见
- L2：检查捕获集合 = ∅，否则编译错误
- L3：捕获非空 → 升级为闭包，按 R5/R6 处理

### 捕获分析（L3）

```
analyze_captures(lambda):
    free_vars = collect_free_vars(body, params)
    for v in free_vars:
        if v.is_value_type:    captures += Snapshot(v)
        elif v.is_reference:   captures += IdentityShare(v)
        elif v.is_method:      captures += BindMethod(v)  // 隐式拉入 this
    return captures
```

### 逃逸分析（决定档 A/B/C）

```
classify(lambda, context):
    match context:
        GenericFnArg(F: (T)->R) + 单态 call site  → TierB
        ConcreteFnArg((T)->R) + !callee_escapes  → TierA
        FieldAssign | Return | SpawnArg | CollectionInsert → TierC
        LocalVarBind → 流分析归类
        _ → TierC（保守兜底）
```

> stdlib 高阶 API（Map/Filter/Reduce）形参标注 `[no_escape]`；用户函数默认假设逃逸，可显式 `[no_escape]` 解锁档 A。

### Send 派生

```
closure_is_send(env) := env.all(c => c.type.is_send())
```

触发点：spawn / SpawnBlocking 调用处。

### IR 指令映射

| 源码模式 | IR |
|---|---|
| 无捕获 lambda | `loadfn anon_fn_N`（与函数引用同源）|
| 档 A | `mkclos.stack env_layout, anon_fn_N` |
| 档 B | 不生成 mkclos；call site inline |
| 档 C | `mkclos.heap env_layout, anon_fn_N` |
| 调用 | 档 A/B：`call`；档 C：`callclos`（vtable）|

### 待内存模型决议后回填
- 档 C env 在 RC vs GC 下的具体编码
- 弱引用支持（独立 IR 指令，不混入闭包 spec）
- VM 诊断（引用链 / env dump）是 follow-up

## Testing Strategy

> **每条 Spec scenario → 至少一个测试用例**。本变更为 design-only，不写代码；测试落地在后续 `impl-lambda-l2` 和 `impl-closure-l3` 变更内。

### 测试矩阵

| Requirement | 测试类型 | 落地变更 |
|---|---|---|
| R1 Lambda 字面量语法 | Parser unit + golden parse | impl-lambda-l2 |
| R2 函数类型 `(T)->R` | Parser + TypeCheck unit | impl-lambda-l2 |
| R3 表达式短写 | Parser unit + golden compile | impl-lambda-l2 |
| R4 Local function | Parser + TypeCheck + golden run | impl-lambda-l2（无捕获）/ impl-closure-l3（捕获）|
| R5 值类型快照 | TypeCheck + Codegen + golden run | impl-closure-l3 |
| R6 引用类型按身份 | TypeCheck + Codegen + golden run | impl-closure-l3 |
| R7 循环变量新绑定 | golden run（for/foreach/while 各一）| impl-closure-l3 |
| R8 spawn move | TypeCheck（Z0809）+ golden run | impl-closure-l3（依赖 spawn）|
| R9 无多播 | Parser 错误用例 | impl-lambda-l2 |
| R10 闭包可比较 | golden run（==/!=/identity）| impl-closure-l3 |
| R11 不可序列化 | TypeCheck 错误用例 | impl-closure-l3 |
| R12 三档实现 + `--warn-closure-alloc` | Codegen + IR snapshot + 编译选项输出 | impl-closure-l3 |
| R13 Ref<T> 共享 | golden run | impl-closure-l3（依赖 Ref<T>）|
| R14 L 阶段限定 | L2 拒绝捕获 / L3 允许 双套 | impl-lambda-l2 + impl-closure-l3 |

### 测试位置约定

**C# 编译器测试** `src/compiler/z42.Tests/`：
- Lexer：`LexerTests.cs` 增 `=>` token
- Parser：`ParserTests/LambdaTests.cs` / `FnTypeTests.cs` / `ExprBodyFnTests.cs` / `NestedFnTests.cs`
- TypeCheck：`TypeCheckerTests/CaptureTests.cs` / `EscapeAnalysisTests.cs`
- Codegen：`CodegenTests/ClosureTests.cs`（IR snapshot）

**Rust VM 测试** `src/runtime/`：
- 单元：`src/closure_tests.rs`（闭包对象、vtable dispatch）
- Golden：`tests/golden/run/closures/r1_lambda.z42` 等按 Requirement 编号

**跨语言端到端**：每个 Requirement 至少一个 `*.z42` golden test，验证编译 + 运行 + 输出。

### 测试用例命名规范

后续 `impl-*` 变更落地时遵循：
- 单元测试方法名：`{R号}_{Scenario关键词}`，如 `R5_IntCaptureSnapshot`
- Golden test 文件名：`r{号}_{scenario}.z42`，如 `r7_foreach_capture.z42`
- 失败用例文件名加 `_error` 后缀：`r9_multicast_error.z42`

### 本变更（add-closures）的验证标准

design-only 变更，无代码；验证：
- ✅ 所有规范文档可读、无 broken link
- ✅ `language-overview.md` / `grammar.peg` / `ir.md` / `concurrency.md` 已同步并交叉引用
- ✅ `docs/roadmap.md` 标记 "closure 设计完成"
- ✅ 与已存的 5 条 feedback memory 无冲突（含本次新增 `feedback_leak_via_diagnostics.md`）
- ✅ 本目录所有文档（proposal / spec / design / tasks）相互一致

## Risk & Open Items

| 风险 | 缓解 |
|------|------|
| 内存模型未定 → 档 C 实现细节模糊 | spec 标注"待回填"；不阻塞设计落地 |
| L2/L3 实现间隔大，L2 决策被 L3 推翻 | L2 严格只做无捕获子集；接口（语法/类型/IR）以本 spec 为准 |
| 逃逸分析准确性 | 初版保守（默认逃逸 → 档 C）；用户 `[no_escape]` 解锁；优化作为 L3 后续 |
| `=>` 在 grammar 中歧义 | Decision 6 已分析；后续 grammar.peg 同步阶段实证 |
