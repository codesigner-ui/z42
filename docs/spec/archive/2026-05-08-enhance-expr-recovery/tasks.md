# Tasks: enhance-expr-recovery

> 状态：🟢 已完成 | 创建：2026-05-08 | 完成：2026-05-08
> 类型：lang（parser 错误报告契约扩展，走完整流程）
> 来源：[review.md](../../../docs/review.md) Part 2 §2.2 + Part 3 §3.7

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1109/1109**（baseline 1104 + 5 新 ParserRecoveryTests）
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**

### 实施落点
- ExprParser 入口加 DiagnosticBag overload，`ParseInternal` 提取主体
- 新增 `SkipToExprBoundary(cursor)` 平面 skip helper
- NudFn / LedFn 委托签名加 `DiagnosticBag? diags`，所有 ~15 个 led handler + 8 个 nud handler 同步更新
- ParseArgList / ParseCallArgWithOptionalModifier thread bag → 函数调用 / 数组字面量 / `new T(args)` 等聚合表达式 per-arg recover 生效
- StmtParser:
  - `StmtFn` 委托加 diags
  - `ParseStmt` / 13 个 `Parse*` handler 加 diags
  - `BlockOrSingle` 加 diags
  - 16 处 `ExprParser.Parse(...)` thread bag
- TopLevelParser:
  - ctor delegation args (line 271)、表达式体方法 (line 294)、字段初始化 (Types.cs:377)、默认参数 (Helpers.cs:42) thread bag
  - `ParseParamList` 加 diags 参数
- 5 个新测试 (ParserRecoveryTests.cs) 验证多错恢复

### Spec 覆盖确认
| Scenario | 测试 | 状态 |
|---|---|---|
| ExprParser 接受 DiagnosticBag（向后兼容 throw）| ParseExpr_PublicApi_StillThrows_BackwardCompat | ✅ |
| 单 expr error → 1 ErrorExpr + 1 diag | SingleBadExprInVarDecl_ReportsErrorAndContinues | ✅ |
| 两个 stmt 各含 expr error → ≥2 diag | TwoBadExprsAcrossStmts_BothErrorsReported | ✅ |
| `f(1, *, 3)` 中间 arg 失败 → 后续 arg 继续 | BadArgInCall_DoesNotBlockSubsequentArgs | ✅ |
| 数组字面量元素恢复 | BadElemInArrayLiteral_OtherElemsParse | ✅ |

### 结论：✅ 全绿，可归档；review.md §2.2 + §3.7 整体收口

## 进度概览
- [ ] 阶段 1: 基础（baseline + helper）
- [ ] 阶段 2: ExprParser 入口改造
- [ ] 阶段 2.5: ArgList / CallArgWithModifier thread bag
- [ ] 阶段 3: 调用站点 thread bag（StmtParser / TopLevelParser*）
- [ ] 阶段 4: 测试 + 验证
- [ ] 阶段 5: 文档同步
- [ ] 阶段 6: 归档 + 提交

## 阶段 1: 基础
- [ ] 1.1 baseline: `dotnet test`: 1104/1104 全绿
- [ ] 1.2 把现有 `ExprParser.Parse(cursor, feat, minBp)` 主体提取为 `ParseInternal`（行为不变，仅重命名）
- [ ] 1.3 添加私有 static helper `SkipToExprBoundary(cursor)` —— sync token: `,` `)` `]` `;` `}` 平面 skip

## 阶段 2: ExprParser 入口 DiagnosticBag overload
- [ ] 2.1 添加 `Parse(cursor, feat, minBp = 0, DiagnosticBag? diags = null)` 重载
- [ ] 2.2 当 `diags != null` 时 try/catch 包裹 ParseInternal；catch 创建 ErrorExpr + skip
- [ ] 2.3 当 `diags == null` 时直接调 ParseInternal（保持向后兼容）

## 阶段 2.5: ArgList / CallArg 改造
- [ ] 2.5.1 `ExprParser.Atoms.cs::ParseArgList` 添加 `DiagnosticBag? diags = null` 参数
- [ ] 2.5.2 `ParseArgList` 内部调用 `Parse(cursor, feat, diags: diags)` 传 bag
- [ ] 2.5.3 `ParseCallArgWithOptionalModifier` 同款 thread bag
- [ ] 2.5.4 `out var x` 中 expected-identifier 路径**保留 throw**（按 design Decision 备注）

## 阶段 3: 调用站点 thread bag
- [ ] 3.1 `StmtParser.cs` 16 个 `ExprParser.Parse(cursor, feat).Unwrap(...)` 调用站点 thread `diags`（来自 ParseBlock 上下文）
- [ ] 3.2 `TopLevelParser.cs:271, 294` 2 个调用站点 thread bag
- [ ] 3.3 `TopLevelParser.Helpers.cs:42` 默认参数值解析 thread bag
- [ ] 3.4 `TopLevelParser.Types.cs:377` 字段初始化 expr 解析 thread bag
- [ ] 3.5 `Parser.cs::ParseExpr()` 公开 test API **保留 OrThrow**（不改，按 design Decision 5）

## 阶段 4: 测试
- [ ] 4.1 新建 `src/compiler/z42.Tests/ParserRecoveryTests.cs`
- [ ] 4.2 Case 1: 单 expr error
  - 输入: `void Main() { var x = 5+; }`
  - 验证: `Diagnostics.HasErrors == true`，含 1 个 error；AST 含 1 个 ErrorExpr
- [ ] 4.3 Case 2: 两个 stmt 各含 expr error
  - 输入: `void Main() { var x = 5+; var y = ; }`
  - 验证: 含 ≥ 2 个 error
- [ ] 4.4 Case 3: 函数调用 bad arg 不阻断后续
  - 输入: `void Main() { Foo(1, *, 3); } void Foo(int a, int b, int c) {}`
  - 验证: args 含 3 个元素（中间 ErrorExpr）；diag 含 1 个 expr error
- [ ] 4.5 Case 4: 数组字面量
  - 输入: `void Main() { int[] a = [1, *, 3]; }`
  - 验证: 3 个元素，含 1 个 ErrorExpr
- [ ] 4.6 Case 5: 向后兼容（不传 bag）
  - 输入: 直接调 `ExprParser.Parse(cursor, feat)`（无 bag）解析 `5+`
  - 验证: 抛 ParseException（旧行为）

## 阶段 5: 验证
- [ ] 5.1 `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error
- [ ] 5.2 `dotnet test`: **1104 + 5 = 1109/1109** 全绿（baseline + 5 新 case）
- [ ] 5.3 `./scripts/test-vm.sh`: 全绿（VM 不受影响）
- [ ] 5.4 spec scenarios 5 个全部命中（对应 4.2-4.6）

## 阶段 6: 文档同步
- [ ] 6.1 `Parser.cs` doc 更新：补"expression 级别 recovery 已激活"说明
- [ ] 6.2 `docs/review.md` Part 2 §2.2 / Part 3 §3.7 状态注记 → 🟢
- [ ] 6.3 `docs/design/compiler/compiler-architecture.md`（按 [CLAUDE.md](../../../.claude/CLAUDE.md) "实现原理文档规则"）补"parser 三级错误恢复（声明/语句/表达式）"段
- [ ] 6.4 `docs/features.md`（如有 parser 错误处理章节）同步

## 阶段 7: 归档 + 提交
- [ ] 7.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 7.2 `docs/spec/changes/enhance-expr-recovery/` → `docs/spec/archive/2026-05-08-enhance-expr-recovery/`
- [ ] 7.3 commit + push
