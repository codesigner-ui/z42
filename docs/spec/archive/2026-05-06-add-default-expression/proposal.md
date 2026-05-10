# Proposal: add-default-expression — `default(T)` zero-value 表达式

## Why

z42 缺 `default(T)` 表达式 — C# / Rust（`<T as Default>::default()` / `T::default()`）/ Java（`null` for ref + 0 for primitive）等价物的明显缺口。

实际场景：
- 数组 / List 初始填充：`var a = new int[N]; for (var i = 0; i < N; i++) a[i] = default(int);`（虽然 z42 当前 `int[]` ObjNew 已自动 zero-init，但 user code 显式 reset 仍需要表达 zero）
- 通用 reset：`field = default(string)` 等价于 `field = null`
- 占位 / placeholder：`Result = default(T)` 在已知 T 的容器代码中（D-8b-1 `MulticastException<R>.Results[i]` 是典型 trigger）
- 模板代码：写"任意 T 的零值"语义比硬编码 `0` / `""` / `null` 更清晰且可移植

z42 已有的 zero-init 机制（VM `default_value_for(type_tag)` + class 字段无 init 时 ObjNew 自动 zero-fill）只在 **类型系统已知** 的语境下触发；用户 expression 层面没有任何方式表达"给我这个类型的零值"。

来源：[docs/deferred.md](docs/deferred.md) D-8b-3，由 D-8b 探索（2026-05-04）发现。

## What Changes

- 解锁 `default(T)` 作为一个**通用一元 prefix expression**，T 是任意 type expression
- TypeChecker 校验 T 可解析，把 expression 类型设为 T，记录到 BoundDefault
- IrGen 按 T 的类型 tag 直接 emit 现有 Const* 系列指令，**无新 IR 指令**：
  - `int`/`long`/数值别名 → `ConstI32(0)` / `ConstI64(0)`
  - `double`/`float` → `ConstF64(0.0)`
  - `bool` → `ConstBool(false)`
  - `char` → `ConstChar('\0')`
  - `string` → `ConstNull`
  - 任意 class / interface / struct / array → `ConstNull`
  - 任意 nullable T? → `ConstNull`
- VM 不变（既有 Const* 已足够）
- `default()` 不带括号或缺类型 → parser 兜底报错（与 switch 的 `default:` label 不冲突，因为后者只在 switch 内部上下文识别）

## Out of Scope（本变更不做）

- **泛型 type-param T 的 `default(T)`**：z42 generic class / function 走 erasure 模型，IR 层无 T 的具体类型；解析需要"运行时 type_args 查询 + 新 IR 指令 DefaultOf(reg)"，与本变更耦合度低。**触发 E0421 InvalidDefaultType 并提示 Phase 2 deferred**。后续单独 spec `add-default-generic-typeparam` 处理（解锁 D-8b-1 `MulticastException<R>.Results[i] = default(R)` 用例）。
- **`default` 字面量（不带 `(T)`）**：C# 7.1+ 的 `T x = default;` 语法，z42 暂不引入；用户必须显式写类型
- **结构体的 zero-init 实例化**：z42 `struct` 当前与 class 共用 reference path，`default(MyStruct)` 返回 null（与 class 一致）；待 struct 真正成为 value type 后再评估
- **expression-level 优化**：常量折叠 / 编译期消除 — IR 已经是最小常量指令，无优化收益

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| [src/compiler/z42.Syntax/Parser/Ast.cs](src/compiler/z42.Syntax/Parser/Ast.cs) | MODIFY | 新增 `DefaultExpr(TypeExpr Target, Span)` AST 节点 |
| [src/compiler/z42.Syntax/Parser/ExprParser.cs](src/compiler/z42.Syntax/Parser/ExprParser.cs) | MODIFY | NudTable 增 `Default` keyword → `default ( <TypeExpr> )` 解析（与 switch `default:` 区分由上下文 — switch 标签解析在 StmtParser 内吃 `default :`，其它位置 default 进入 expr nud）|
| [src/compiler/z42.Semantics/Bound/BoundExpr.cs](src/compiler/z42.Semantics/Bound/BoundExpr.cs) | MODIFY | 新增 `BoundDefault(Z42Type Type, Span)` |
| [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs) | MODIFY | DefaultExpr 分支：解析 T，校验非 generic-type-param，BoundDefault.Type=T |
| [src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs](src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) | MODIFY | `BoundDefault` 分支按 Type emit Const* |
| [src/compiler/z42.Core/Diagnostics/Diagnostic.cs](src/compiler/z42.Core/Diagnostics/Diagnostic.cs) | MODIFY | 新增 `E0421 InvalidDefaultType` |
| [src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs](src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs) | MODIFY | E0421 catalog 条目 |
| `src/tests/operators/default_primitives/` | NEW | golden：default(int)/long/double/bool/char + 字符串拼接打印 |
| `src/tests/operators/default_string/` | NEW | golden：default(string) == null |
| `src/tests/operators/default_class/` | NEW | golden：default(MyClass) == null + null check |
| `src/tests/operators/default_array/` | NEW | golden：default(int[]) == null + new int[N] 等价 |
| `src/tests/errors/421_invalid_default_type/` | NEW | E0421 错误用例（generic type-param + 未知类型） |
| [src/compiler/z42.Tests/DefaultExpressionTests.cs](src/compiler/z42.Tests/DefaultExpressionTests.cs) | NEW | 单测：parser / typechecker / emitter 三层契约 |
| [docs/design/language-overview.md](docs/design/language-overview.md) | MODIFY | 加 default(T) expression 段 |
| [docs/deferred.md](docs/deferred.md) | MODIFY | D-8b-3 移到"已落地"，新增 add-default-generic-typeparam Phase 2 deferred 占位 |

**只读引用**：

- [src/runtime/src/metadata/types.rs](src/runtime/src/metadata/types.rs) — `default_value_for(type_tag)` 现有 zero-init 表（确认 codegen 与 VM 表对齐）
- [src/compiler/z42.IR/IrModule.cs](src/compiler/z42.IR/IrModule.cs) — 现有 Const* 指令家族
- [src/compiler/z42.Syntax/Lexer/TokenDefs.cs:35](src/compiler/z42.Syntax/Lexer/TokenDefs.cs#L35) — `default` 已是 keyword（switch label 用），无 lexer 改动

## Open Questions

无（关键决策由 deferred 序列已钉死：先 Phase 1 fully-resolved，generic-T 留作独立 spec；与 D-8b-2 catch-by-type 解耦推进）。
