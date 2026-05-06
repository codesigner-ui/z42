# Tasks: add-default-expression

> 状态：🟢 已完成 | 创建：2026-05-06 | 完成：2026-05-07 | 类型：lang/ir（完整流程；VM 0 改动）

## 进度概览

- [x] 阶段 1: AST + Bound 节点
- [x] 阶段 2: Parser nud
- [x] 阶段 3: TypeChecker + E0421
- [x] 阶段 4: IrGen Const* 分发
- [x] 阶段 5: Tests
- [x] 阶段 6: 文档同步 + 验证 + 归档

---

## 阶段 1: AST + Bound 节点

- [x] 1.1 [Ast.cs](src/compiler/z42.Syntax/Parser/Ast.cs) 增 `DefaultExpr(TypeExpr Target, Span Span) : Expr`
- [x] 1.2 [BoundExpr.cs](src/compiler/z42.Semantics/Bound/BoundExpr.cs) 增 `BoundDefault(Z42Type Type, Span Span) : BoundExpr`

## 阶段 2: Parser nud

- [x] 2.1 [ExprParser.cs](src/compiler/z42.Syntax/Parser/ExprParser.cs) NudTable 注册 `TokenKind.Default`：消费 `default ( <TypeExpr> )` → DefaultExpr
- [x] 2.2 验证：`switch (x) { default: ... }` 现有 goldens 全 PASS（与 statement-level switch label 路径不冲突）

## 阶段 3: TypeChecker + E0421

- [x] 3.1 [Diagnostic.cs](src/compiler/z42.Core/Diagnostics/Diagnostic.cs) 增 `InvalidDefaultType = "E0421"`
- [x] 3.2 [DiagnosticCatalog.cs](src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs) E0421 条目（title + message + 4 例）
- [x] 3.3 [TypeChecker.Exprs.cs](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs) `case DefaultExpr de` 分支：
  - 调用 ResolveTypeExpr（z42 既有 type expr 解析器）
  - Z42ErrorType / Z42GenericParamType → E0421
  - 否则返回 BoundDefault(t, span)

## 阶段 4: IrGen Const* 分发

- [x] 4.1 [FunctionEmitterExprs.cs](src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) `case BoundDefault bd` 分支：
  - Z42PrimType 数值 → ConstI64(0) + IrType.I64
  - "double"/"float" → ConstF64(0.0) + IrType.F64
  - "bool" → ConstBool(false) + IrType.Bool
  - "char" → ConstChar('\0') + IrType.Char
  - "string" / class / interface / array / nullable → ConstNull + IrType.Ref

## 阶段 5: Tests

- [x] 5.1 `src/tests/operators/default_primitives/` golden（int/long/double/bool/char/byte 各一行打印）
- [x] 5.2 `src/tests/operators/default_string/` golden（null check + `?? "<null>"` 输出）
- [x] 5.3 `src/tests/operators/default_class/` golden（自定义 class，null 检查 + `?.` 访问）
- [x] 5.4 `src/tests/operators/default_array/` golden（int[] / string[] null 验证 + `new int[N]` 比对）
- [x] 5.5 `src/tests/errors/421_invalid_default_type/` 错误用例（generic type-param + unknown type）
- [x] 5.6 [src/compiler/z42.Tests/DefaultExpressionTests.cs](src/compiler/z42.Tests/DefaultExpressionTests.cs) 单测：
  - Parser 4 例（OK / dotted / 无 parens / 空）
  - TypeChecker 4 例（int OK / unknown E0421 / generic param E0421 / class OK）
  - IrGen 5 例（int → ConstI64(0) / double → ConstF64(0.0) / bool → ConstBool(false) / char → ConstChar('\0') / class → ConstNull）
- [x] 5.7 `regen-golden-tests.sh` 重生新 case 的 .zbc

## 阶段 6: 文档同步 + 验证 + 归档

- [x] 6.1 [docs/design/language-overview.md](docs/design/language-overview.md) 加 `default(T)` expression 段（语义表 + 几个示例）
- [x] 6.2 [docs/deferred.md](docs/deferred.md) D-8b-3 移到"已落地"，写明 Phase 1 范围 + Phase 2 generic-T 留作独立 spec `add-default-generic-typeparam`
- [x] 6.3 验证全套：
  - `dotnet build src/compiler/z42.slnx` 无错
  - `cargo build --manifest-path src/runtime/Cargo.toml` 无错（VM 0 改动，build 即过）
  - `dotnet test` 全过（含新 DefaultExpressionTests + 21+ switch 回归）
  - `./scripts/test-vm.sh interp/jit` 全过（4 个新 default golden + 现有所有）
  - `./scripts/test-cross-zpkg.sh` 全过
  - `cargo test --manifest-path src/runtime/Cargo.toml` 全过
- [x] 6.4 commit + push（`feat(lang): add default(T) expression`）
- [x] 6.5 归档 `spec/changes/add-default-expression/` → `spec/archive/2026-05-06-add-default-expression/`

---

## 备注

### 验证场景与 spec.md scenarios 的映射

| spec scenario | 验证位置 |
|--------------|---------|
| Numeric primitives default to 0 | golden `default_primitives/`（int/long/double/byte 表达式参与算术）|
| bool defaults to false | golden `default_primitives/`（默认值打印）|
| char defaults to '\0' | golden `default_primitives/`（与 `'a'` 比较）|
| string defaults to null | golden `default_string/` + DefaultExpressionTests |
| class / interface / array / nullable → null | golden `default_class/` + `default_array/` |
| usable in expression context | golden `default_primitives/`（参与 +、??、ToString）|
| Unknown type rejected | golden errors `421_invalid_default_type/` + DefaultExpressionTests |
| Generic type-parameter rejected | golden errors `421_invalid_default_type/` + DefaultExpressionTests |
| Type expression syntactically valid but unresolvable | DefaultExpressionTests（dotted path with unknown inner）|
| `default :` label inside switch | 现有 switch goldens 回归（确保 parser 路径不冲突）|
| `default(T)` as statement-level expression | 所有 default_* goldens |
| `default` 不带括号 / 缺 type | DefaultExpressionTests parser 单测 |
