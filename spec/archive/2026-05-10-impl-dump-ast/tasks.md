# Tasks: Implement --dump-ast and --dump-bound

> 状态：🟢 已完成 | 创建：2026-05-10 | 完成：2026-05-10
> 类型：refactor（最小化模式，per workflow.md）
> 关联：[docs/review.md](../../../docs/review.md) Part 3 §3.2（Clang `-ast-dump` 对标）

**变更说明：** 实现 `--dump-ast` handler（当前是 `Console.WriteLine(cu)` 占位）+ 新增 `--dump-bound` flag（dump TypeChecker 输出的 Bound 树，用 introduce-bound-visitor 提供的 `BoundExprVisitor<Unit>` 几乎免费实现）。

**原因：** 调试 typecheck / codegen 错误时不必读源码翻 IR；CI 失败 paste 树形 dump 可比对。Clang `-ast-dump` / `-ast-print` 模式验证过的高频价值。

**文档影响：**
- `src/compiler/z42.Pipeline/README.md` 增 dumper 文件
- `docs/dev.md` "调试技巧" 段（如有）增 dump 示例

**Scope（允许改动的文件）**

| 文件路径 | 变更 | 说明 |
|---------|------|------|
| `src/compiler/z42.Pipeline/AstDumper.cs` | NEW | 手写递归 switch over AST records → 缩进树字符串 |
| `src/compiler/z42.Pipeline/BoundDumper.cs` | NEW | `BoundExprDumper : BoundExprVisitor<Unit>` + `BoundStmtDumper : BoundStmtVisitor<Unit>`，写入共享 StringBuilder |
| `src/compiler/z42.Pipeline/SingleFileCompiler.cs` | MODIFY | `dumpAst` 路径改用 `AstDumper`；新增 `dumpBound` 参数 + 调用 `BoundDumper` |
| `src/compiler/z42.Driver/Program.cs` | MODIFY | 加 `--dump-bound` Option + 传到 SingleFileCompiler |
| `src/compiler/z42.Tests/AstDumperTests.cs` | NEW | 解析小段源码 → dump → 字符串断言 |
| `src/compiler/z42.Tests/BoundDumperTests.cs` | NEW | 解析+绑定小段源码 → dump → 类型注解 + 缩进结构断言 |
| `src/compiler/z42.Pipeline/README.md` | MODIFY | 加 dumper 文件说明 |

**Out of Scope:**
- 不引入 AST visitor 框架（独立 follow-up `introduce-ast-visitor` spec；本 spec 用手写 switch）
- 不实现 IR dumper（已有 `--dump-ir` 走 ZasmWriter）
- 不实现 `--dump-tokens-json` / `--dump-ast-json` 等结构化输出（pre-1.0 不必）
- 不实现 dump 到文件的 `--dump-out <path>` flag（先支持 stdout，需要时再加）

---

## 进度概览

- [x] 阶段 1: AstDumper（手写递归打印）
- [x] 阶段 2: BoundDumper（visitor-based）
- [x] 阶段 3: 接入 CLI + SingleFileCompiler
- [x] 阶段 4: 单元测试
- [x] 阶段 5: 文档同步 + 归档

---

## 输出格式约定

```
NodeKind <salient-attr> [: type-annotation]? <span-display>
  ChildField: ChildKind ...
  ChildField: ChildKind ...
```

具体规则：
- **缩进**：每层 2 空格
- **NodeKind**：节点 C# 类型名（去掉 `Bound` 前缀对 Bound 树）
- **salient-attr**：节点的关键描述属性（标识符 → name；字面量 → 值；运算符 → op；其他 → 省略）
- **类型注解**：仅 Bound 节点有，格式 `: <Z42Type>`（用 `Z42Type.ToString()` 现有实现）
- **span-display**：`(line:col)` 或 `(line:col-line:col)` 短形式；缺省的 child span 不重复打印
- **child 标签**：复合节点列出每个字段的逻辑名（"Cond:", "Then:", "Else:" 等），子节点接在标签后另起一行缩进
- **List<T> 字段**：`[N items]:` 然后每元素另起一行缩进
- **null / 空 list**：省略不打印

样例（AST）：

```
CompilationUnit (test.z42)
  Items [1 items]:
    FunctionDecl Add (1:1)
      Params [2 items]:
        Param a: NamedType int (1:14)
        Param b: NamedType int (1:21)
      ReturnType: NamedType int (1:30)
      Body: BlockStmt (1:35)
        ReturnStmt (2:5)
          BinaryExpr Add (2:12)
            Left: IdentExpr a (2:12)
            Right: IdentExpr b (2:16)
```

样例（Bound，类型注解）：

```
BoundBlock (1:35)
  Stmts [1 items]:
    BoundReturn (2:5)
      Value: BoundBinary Add : int (2:12)
        Left: BoundIdent a : int (2:12)
        Right: BoundIdent b : int (2:16)
```

---

## 阶段 1: AstDumper

- [x] 1.1 NEW [src/compiler/z42.Pipeline/AstDumper.cs](../../../src/compiler/z42.Pipeline/AstDumper.cs):
  - `public static class AstDumper { public static string Dump(CompilationUnit cu) }`
  - 内部 `private sealed class Writer` 持 `StringBuilder _sb` + `int _indent`，提供 `Line(string)` / `Indent()`/`Dedent()` 帮助方法
  - `private static void Visit(Item)` / `Visit(Stmt)` / `Visit(Expr)` / `Visit(TypeExpr)` 分派 switch 覆盖所有 AST sealed record
  - Span 渲染辅助 `FormatSpan(Span s)` → `(line:col)` 短形式
  - 入口 `Dump(CompilationUnit cu)` 返回完整树字符串（含末尾换行）
- [x] 1.2 `dotnet build` —— 编译通过

## 阶段 2: BoundDumper

- [x] 2.1 NEW [src/compiler/z42.Pipeline/BoundDumper.cs](../../../src/compiler/z42.Pipeline/BoundDumper.cs):
  - `public static class BoundDumper { public static string Dump(SemanticModel model) }`
  - `private sealed class StmtDumper : BoundStmtVisitor<Unit>` + `private sealed class ExprDumper : BoundExprVisitor<Unit>` —— 共享 StringBuilder + indent state via 构造函数注入的 `Writer`
  - 每个 `VisitXxx` 输出节点行 + 通过 `_writer.Indent()` / `Dedent()` 控制子节点缩进；调用 `_exprDumper.Visit(child)` / `_stmtDumper.Visit(child)` 递归
  - 类型注解：`BoundExpr.Type.ToString()` 已存在
  - Dump 入口遍历 `model.BoundBodies.Values` 每个 BoundBlock
- [x] 2.2 `dotnet build` —— 编译通过

## 阶段 3: CLI 接入

- [x] 3.1 MODIFY [src/compiler/z42.Driver/Program.cs:127](../../../src/compiler/z42.Driver/Program.cs#L127):
  - 在 `dumpAstOpt` 之后加 `var dumpBoundOpt = new Option<bool>("--dump-bound", "Print bound tree (after typecheck) and exit")`
  - `rootCmd.AddOption(dumpBoundOpt)` + handler 抽 `var dumpBound = ctx.ParseResult.GetValueForOption(dumpBoundOpt)`
  - `SingleFileCompiler.Run(...)` 传入 `dumpBound`
- [x] 3.2 MODIFY [src/compiler/z42.Pipeline/SingleFileCompiler.cs:43](../../../src/compiler/z42.Pipeline/SingleFileCompiler.cs#L43):
  - 添加 `bool dumpBound` 参数
  - `dumpAst` 路径：`Console.Write(AstDumper.Dump(cu)); return 0;`
  - 在 typecheck 完成后（已有 PipelineCore.CheckAndGenerate 之后）：
    - 但 PipelineCore 内部把 typecheck + codegen 一起做了；需要确认 SemanticModel 是否暴露
    - 如果不暴露，把 dumpBound 路径插入到 typecheck 但不做 codegen 的位置（可能需要拆分或加可选 emit-skip）
- [x] 3.3 探查 [src/compiler/z42.Pipeline/PipelineCore.cs](../../../src/compiler/z42.Pipeline/PipelineCore.cs)：
  - 看 `CheckAndGenerate` 是否有早出路径或 SemanticModel exposure；必要时加一个 `CheckOnly(cu, …)` 或扩展返回值
  - 决策：**优先在 PipelineCore 加 `CheckOnly(cu, …) → SemanticModel?`** 而不是把 dumpBound 散到 SingleFileCompiler

## 阶段 4: 单元测试

- [x] 4.1 NEW [src/compiler/z42.Tests/AstDumperTests.cs](../../../src/compiler/z42.Tests/AstDumperTests.cs):
  - `Dump_HelloWorld_PrintsExpectedTree`：`fn Main() : void { print("hi"); }` → 字符串断言含期望子串
  - `Dump_BinaryExpr_RendersOperatorAndOperands`
  - `Dump_NestedBlockStmt_HasCorrectIndentation`
- [x] 4.2 NEW [src/compiler/z42.Tests/BoundDumperTests.cs](../../../src/compiler/z42.Tests/BoundDumperTests.cs):
  - `Dump_TypedAddition_AnnotatesIntType`：`fn Add(a: int, b: int): int { return a + b; }` → 含 `: int` 注解
  - `Dump_IfElse_RecursesIntoBothBranches`
- [x] 4.3 `dotnet test` 全绿

## 阶段 5: 文档同步 + 归档

- [x] 5.1 MODIFY [src/compiler/z42.Pipeline/README.md](../../../src/compiler/z42.Pipeline/README.md):
  - 核心文件表加 `AstDumper.cs` / `BoundDumper.cs`
- [x] 5.2 MODIFY [docs/review.md](../../../docs/review.md):
  - Part 3 §3.2 / 路线图 `impl-dump-ast` 状态 📋 → 🟢 2026-05-10
  - 优先级清单移除 `impl-dump-ast`（只剩 `split-symbol-from-type`）
  - 修订记录追加
- [x] 5.3 tasks.md 状态改 🟢 已完成
- [x] 5.4 移动 `spec/changes/impl-dump-ast/` → `spec/archive/2026-05-10-impl-dump-ast/`
- [x] 5.5 commit + push

## 备注

### 设计要点（写到这里以避免开 design.md，符合 minimal mode）

- **格式选择**：缩进树是最小可读形态，比 `Console.WriteLine(cu)`（C# 默认 record ToString，单行 `ClassName { Prop1 = Value, Prop2 = Value, ... }`，对深嵌套树几乎不可读）大幅改进。S-expr / JSON 留 follow-up
- **类型注解仅 Bound**：AST 阶段 TypeExpr 是用户写的语法形式（`int` / `string` / `Foo<Bar>`），dump 已能看到节点本身；inferred 类型只在 Bound 阶段才有
- **共享 Writer**：BoundDumper 内部 `StmtDumper` 和 `ExprDumper` 必须共享 `StringBuilder` + `_indent` 状态，否则缩进对不齐——构造函数注入是清晰方案
- **PipelineCore 拆分**：`--dump-bound` 需要 typecheck 输出但不需要 codegen。如果 `CheckAndGenerate` 已是单一原子函数，则在它内部按 flag 早出 / 拆出 `CheckOnly` helper。具体看 3.3 探查结果
- **Span 短显示**：用 `(line:col)`；range 形式 `(line:col-line:col)` 仅在节点跨越多行 (start.Line ≠ end.Line) 时显示
- **List/Optional 渲染**：列表节点头打印 `[N items]:`（即使 N=0 也省略整行，避免噪声）；Optional null 字段直接不打印（约定 "缺省不出现"）

### 风险

- **风险 1**：AST 节点种类多（77+），手写 switch 容易漏 → 缓解：先把 default case throw NotSupported，CI 跑过的测试覆盖发现遗漏；后续 introduce-ast-visitor spec 再用 visitor 强制 exhaustive
- **风险 2**：PipelineCore.CheckAndGenerate 拆分可能牵连其他 caller → 缓解：保持 `CheckAndGenerate` 不变，仅 ADD `CheckOnly` helper，零回归
- **风险 3**：dump 输出过大（大文件）影响 CI 输出 → 缓解：仅在显式 `--dump-*` 时输出，默认不开
