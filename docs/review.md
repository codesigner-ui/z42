# z42 工程与编译器架构审查报告

> **日期**: 2026-05-05
> **范围**: `src/compiler/`、`src/runtime/`（辅助参考）、`docs/`、`spec/`
> **目的**: 识别当前代码框架的扩展性瓶颈与待处理问题，输出后续迭代路线图
> **性质**: 一次性审查报告（非长期规范）；推进过程中各项落地后可在本文件标记状态，迭代完成后归入 `docs/archive/`

---

## 总览

z42 编译器整体设计**方向正确**（两阶段树：AST + Bound 树；阶段化 pipeline；不可变 sealed record；接口契约清晰），与 Roslyn 的核心思想一致。但在两个维度存在**会随特性增长持续放大**的成本：

1. **工程层**: 4 个 C# 文件已超过 [code-organization.md](../.claude/rules/code-organization.md) 规定的 500 LOC 硬限；同一个"巨型 switch 分发"结构在 3 处复制（编译器 2 处 + VM 1 处）
2. **架构层**: 缺失三项 Roslyn 级编译器的"基础脚手架"——Visitor 框架、Symbol 层级、Parser 错误恢复——它们的缺位让"加一个表达式节点"或"加一个反射 API"的成本近乎线性放大

本报告分两部分：**Part 1** 是工程组织视角的具体问题清单，**Part 2** 是与 Roslyn / TypeScript Compiler / rustc 的设计对标。文末给出按"对未来工作的阻塞程度"排序的推进路线图。

---

## Part 1 — 代码组织与工程问题

### P0 — 直接违反规则、阻塞下一阶段

#### 1.1 四个 C# 文件超 500 LOC 硬限制

[code-organization.md](../.claude/rules/code-organization.md) 规定 C# 文件硬限 500 LOC。当前违规：

| 文件 | LOC | 主要责任 | 建议拆分 |
|---|---|---|---|
| [FunctionEmitterExprs.cs](../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) | 806 | 33 个 `case` 表达式分发 | 按表达式类别拆为 `…ExprLiterals` / `…ExprBinary` / `…ExprMember` 三个 partial |
| [IrGen.cs](../src/compiler/z42.Semantics/Codegen/IrGen.cs) | 759 | 模块级 codegen + dispatcher | 抽 `ItemEmitter` 子类 |
| [ImportedSymbolLoader.cs](../src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs) | 675 | Phase0–Phase3 五段串联 | 每个 phase 独立类 + 协调器 |
| [TypeChecker.Calls.cs](../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs) | 666 | 调用绑定 + 重载解析 | 抽 `OverloadResolver` |

> **注意**: [FunctionEmitterExprs.cs](../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) 正在被 `extend-signature-whitelist` spec 修改。建议**先完成当前 spec**，再单独立 spec 做拆分迭代——避免在已 800+ 行的文件上继续叠加。

#### 1.2 Rust 端 [exec_instr.rs](../src/runtime/src/interp/exec_instr.rs) 756 LOC

解释器主分发，单一巨型 `match`，50+ arms。每加一个新 IR opcode 都会更胖，是 M7 metadata + reflection 工作的潜在瓶颈。

**建议**: 按 op 类别（算术/内存/控制流/调用）抽成 `exec_arith.rs` 等子文件，主 `match` 仅做 dispatch。

### P1 — 现阶段应处理

#### 1.3 三处"巨型 switch 分发"是同一结构问题的复制粘贴

- [FunctionEmitterExprs.cs](../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) — `Bound → IR`
- `TypeChecker.Exprs.BindExpr` — `AST → Bound`
- [exec_instr.rs](../src/runtime/src/interp/exec_instr.rs) — `IR → 执行`

每加一个新表达式/指令要同步改三处。短期接受，但建议在 [docs/design/compiler-architecture.md](design/compiler-architecture.md) 记一笔"统一访问者模式或 dispatch 表"作为 L2 后期重构候选——避免被 M7/L3 持续放大。**根本对策见 Part 2 §2.1**。

#### 1.4 测试文件单文件过大

| 文件 | LOC | 处理方式 |
|---|---|---|
| [TypeCheckerTests.cs](../src/compiler/z42.Tests/TypeCheckerTests.cs) | 1730 | 按特性拆为 `…Generics.cs` / `…Calls.cs` / `…Inheritance.cs` |
| `rc_heap_tests.rs` | 1120 | 按 GC 测试类别（allocation / weak refs / collection / finalization）拆 |

按 [code-organization.md](../.claude/rules/code-organization.md) "测试文件 ≤600 LOC 软限"超出较多。**这种拆分风险极低，零代码变更，纯文件搬运**。

#### 1.5 两个未跟踪的 spec 目录处于"挂起"状态

```
spec/changes/add-array-base-class/        # 仅 proposal，无 design/tasks
spec/changes/extend-signature-whitelist/  # design + tasks 齐全，正在实施
```

- `add-array-base-class` 阻塞 D-8b-1（见 [docs/deferred.md](deferred.md)）。建议在 `extend-signature-whitelist` 收尾后立即推进 design 评审。
- 这两个目录 `??` 状态说明**未纳入 git**——按 [CLAUDE.md](../.claude/CLAUDE.md) "spec/ 必须纳入提交"的工作流要求，需在下一次 commit 一并加入。

### P2 — 长期 / 观察

#### 1.6 文档体量已经很大（design/ ≈ 14k 行）

- [generics.md](design/generics.md) 1179 行、[project.md](design/project.md) 964 行、[compiler-architecture.md](design/compiler-architecture.md) 864 行
- 体量本身没问题，但 [stdlib-organization](design/) / [stdlib-roadmap](design/) 已被 memory 标注为"过程文档不能当硬约束"

**建议**: 在文档头部加 banner 标记类型（**规范 / 过程 / 历史**），避免新接手者把过程文档当真理。

#### 1.7 JIT 占位 [translate.rs](../src/runtime/src/jit/translate.rs) 833 LOC

按 [CLAUDE.md](../.claude/CLAUDE.md) "M4 全绿前不填 JIT/AOT" 的约束，是预期占位。但 833 行的占位本身也是负担——确认是脚手架还是误植，必要时缩成最小骨架直至真正实施。

#### 1.8 代码非常干净的几点（参考，不是问题）

- 全仓 **0 处** TODO/FIXME/HACK 注释
- **0 个** 跳过/忽略测试（757 个 C# Fact + 106 golden VM 场景全跑）
- 项目层依赖单向、无环
- spec/ 目录有 proposal/design/tasks 三段式约束，规范化程度高

---

## Part 2 — 与 Roslyn 等成熟编译器的设计对标

### P0 — 增加新特性时的最大税收

#### 2.1 缺失 Visitor / Rewriter 框架（最痛点）

所有树遍历都是手写 switch，**没有 `SyntaxVisitor<T>` / `SyntaxRewriter` 基类**。

**代价**: 加一个 `BoundXxx` 节点要改 5–15 个文件。Roslyn 用 `CSharpSyntaxVisitor<TResult>` + `partial` 自动派发，加节点只改基类生成的 visitor 和**显式实现的 case**。

**建议**: 引入 `BoundExprVisitor<T>` 抽象基类（不必上代码生成，sealed record 已经够好）：

```csharp
abstract class BoundExprVisitor<T> {
    public T Visit(BoundExpr e) => e switch {
        BoundLiteral x => VisitLiteral(x),
        BoundBinary  x => VisitBinary(x),
        BoundCall    x => VisitCall(x),
        ...
    };
    protected abstract T VisitLiteral(BoundLiteral e);
    protected abstract T VisitBinary(BoundBinary e);
    ...
}
```

单点改写（添加 case），多处 emitter / typechecker / flow 共用。**这一步能立刻把 Part 1 §1.1 的文件膨胀压回去**——`FunctionEmitterExprs.cs` 800+ 行的核心负担就是 33 个 case 的方法体；切到 visitor 后每个 case 一个方法，自然分散到子类。

#### 2.2 Parser 用 `ParseException` 而非错误恢复

[StmtParser.cs](../src/compiler/z42.Syntax/Parser/StmtParser.cs) 等抛 `ParseException`，仅在声明边界 catch。**等于一文件里第一个语法错误后剩余信息丢失**。

| 编译器 | 策略 |
|---|---|
| Roslyn / TypeScript | 错误恢复 + skip-token + 继续解析，一次报告多个错误 |
| rustc | 同上，加上"建议修复" |
| **z42** | parser panic 到声明边界 |

**当前后果**: IDE 集成时（哪怕做一个最小 LSP）单次 diagnostics 报告很差。

**建议**: parser 内部仍可 throw，但在**语句/表达式边界**做 `try/catch + recover-to-next-stmt`，把错误塞进 `DiagnosticBag` 而非中断。这是 LSP 工作的前置条件。

#### 2.3 没有 Symbol 层级，类型即符号

[Z42Type.cs](../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) 把成员表挂在 `Z42ClassType` 上。**没有独立的 `IFieldSymbol` / `IMethodSymbol` / `IParameterSymbol` / `INamespaceSymbol`**。

Roslyn 的 `ISymbol` 是反射 API、IDE 跳转、重命名重构、source generator 的基石。

**当前痛点已经显现**: [ImportedSymbolLoader.cs](../src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs) 675 行的复杂度，本质上是在"用类型对象表达符号"——三阶段加载就是因为类型实例和符号身份混在一起。

**建议**: 在 M7 反射（R-series）启动**之前**做这个抽象——R-series 一旦落地基于 `Z42Type`，回头改的成本会翻倍。这也是 [feedback_design_integrity](../.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/feedback_design_integrity.md) "设计不匹配需求时停下" 的典型场景。

### P1 — 影响中期工具链

#### 2.4 没有公共 API 边界

所有 `Z42.Semantics.*` 都是 `internal`。CLI 是唯一入口。**未来 LSP / formatter / linter 必须做一次大规模 visibility lift**。

**建议**: 现在就在 [docs/design/compiler-architecture.md](design/compiler-architecture.md) 里画一条线，明确"公共 API surface"是哪几个接口（比如 `ISymbolBinder` / `ITypeInferrer` / `SemanticModel` / `Diagnostic`），新代码逐步迁过去。**不需要现在 public**——只要约定好"以后这些会 public"，避免随手把破坏性改动塞到核心类型里。

#### 2.5 Trivia（注释/空白）丢失

Lexer 直接吃掉。**永远做不出 formatter 和保留注释的代码生成**。CLI 已有 `fmt` 子命令占位。如果未来要做实，需要在 lexer 端保留 trivia attached 到 token 上（Roslyn 模式）。

**建议**: 这是个**单向门**——一旦下游代码（比如 `add-array-base-class` 的 codegen scaffolding）开始假设 AST 没注释，回头加 trivia 要改大量代码。建议在 lexer 里**先加 trivia 字段（哪怕暂时全空）**，把扩展点留好。低风险、高未来收益的预留动作。

#### 2.6 Native 签名解析的双轨制

[ManifestSignatureParser.cs](../src/compiler/z42.Semantics/Synthesis/ManifestSignatureParser.cs) 本质是个**微型类型解析器**，与主 type parser 并行存在。`extend-signature-whitelist` spec 正在给它加 `Array` / `Object` 白名单。

**问题**: 每加一种类型形态，要改两处。M7 反射要"运行时类型"统一表示，这条裂缝会更扎眼。

**正面评价**: 把 native import 做成"合成 AST 节点"（[NativeImportSynthesizer.cs](../src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs)）让 Codegen 不感知 FFI——这是好设计，应在 architecture 文档里点名作为正面案例。

**建议**: 当前 spec 收尾后，单独立一个 spec 评估"白名单是否升级为子集 type-parser"。先不行动，先记账。

### P2 — 长期 / 可观察

#### 2.7 文件级增量编译够用

[IncrementalBuild.Probe.cs](../src/compiler/z42.Pipeline/IncrementalBuild.Probe.cs) 按 SHA-256 + 依赖判定，已经够好。Roslyn 红绿树带来的"语句级增量"在大 monorepo 才有收益，z42 短期不需要。

#### 2.8 没有 `#if` / source generator / analyzer

有意为之，Bootstrap 阶段不必引入。但 [docs/deferred.md](deferred.md) 应当明确记录"分析器框架不进 L2/L3"，避免未来误以为是遗忘。

#### 2.9 单线程编译

`PackageCompiler` 串行处理文件。L1/L2 量级不是瓶颈。M7 之后如果 stdlib 持续膨胀，按文件并行（parse + symbol-collect 阶段天然可并）应当容易加——前提是 §2.1 的 visitor 框架先就位。

### 与 Roslyn 的设计取舍总评

| 维度 | z42 选择 | Roslyn | 是否合理 |
|---|---|---|---|
| AST 表达 | sealed record + pattern match | red/green tree + visitor | ✅ 合理（pre-1.0 简洁） |
| Bound 树 | 独立 BoundExpr/BoundStmt | BoundNode 层级 | ✅ 一致，方向正确 |
| 类型 vs 符号 | 合一在 `Z42Type` | `ITypeSymbol` ⊂ `ISymbol` | ❌ **该分** |
| 错误恢复 | parser panic | 错误恢复 + 多错聚合 | ❌ **该改** |
| Visitor 框架 | 无，手写 switch | `SyntaxVisitor<T>` | ❌ **该加** |
| 公共 API | 全 internal | 大量 public | ⚠️ 现在 OK，**该划线** |
| Trivia 保留 | 丢弃 | 完整保留 | ⚠️ **该预留** |
| 增量编译 | 文件级 | 语句级 (red/green) | ✅ 合理 |
| 并行 | 无 | 有 | ✅ 合理 |
| 诊断码组织 | 扁平 E0000–E0916 | 分类 + 严重级 + 类别 | ⚠️ 现在 OK，量大时再分类 |

---

## Part 3 — Clang 视角补充

Roslyn 与 Clang 在很多点上结论相同（Visitor 缺失、Symbol 层、错误恢复——已在 Part 2 覆盖），但 Clang 视角下浮现出几条**Roslyn 视角未覆盖的新发现**，以及几条**值得在文档里点名的 z42 正面设计**。

### P1 — Clang 视角的新发现

#### 3.1 Decl 身份在 pipeline 中丢失

z42 AST 有 `FunctionDecl` / `ClassDecl` / `FieldDecl` / `Param`（[Ast.cs](../src/compiler/z42.Syntax/Parser/Ast.cs)），但**SymbolCollector 之后这些 Decl 节点被折叠进 [Z42Type.cs](../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) 的成员表**，Bound 树和 Codegen 阶段无法回溯到原始 `FunctionDecl`。

Clang 的 `Decl` 类层级在整个 pipeline **保持身份**，可以从任意 `Expr` 节点反向问到"被引用的 Decl 在哪声明"。

**实际影响**:
- 错误消息无法说"X 声明于 file:line"（仅能用名字字符串描述）
- M7 反射 R-series 要求"运行时取成员定义位置"会撞上这个空洞
- 未来 LSP go-to-definition 必须额外维护一份 `name → Decl span` 的映射

**与 §2.3 的关系**: §2.3 "Symbol 层级" 解决**身份模型**（`IFieldSymbol` / `IMethodSymbol`），本项解决**身份的生命周期**（Decl 不应在 SymbolCollector 后死亡）。两者互补，**应在同一个 spec `split-symbol-from-type` 中一并设计**——抽 Symbol 时直接让 Symbol 持有 `DeclSpan` 字段，从根上消除丢失。

#### 3.2 `--dump-ast` 标志已接但未实现

CLI [Program.cs](../src/compiler/z42.Driver/Program.cs) 已经声明 `--dump-ast` 参数，但 handler 是空的。Clang 的 `-ast-dump` / `-ast-print` / `-ast-dump=json` 是开发期最常用的调试工具之一。

**建议**: 立小 spec `impl-dump-ast`（`refactor` 类型，最小化模式即可）：
- 实现 `BoundExpr.ToTreeString()` 之类的递归打印（实际上 §2.1 引入 `BoundExprVisitor<T>` 后，dumper 就是一个 `BoundExprVisitor<string>` 实现，几乎免费得到）
- 同步对 AST `CompilationUnit` 也做一份
- 输出格式：缩进树 + 类型注解 + Span

**收益**: 调试 typecheck / codegen 错误时不必读源码，打 dump 即可。CI 失败时也能贴 dump 给同事看。**低成本、高频价值**。

#### 3.3 DiagnosticEngine 缺乏成熟特性

[Diagnostics/](../src/compiler/z42.Core/Diagnostics/) 是简单 `List<Diagnostic>` + 静态 severity，没有：

| 特性 | Clang 形态 | z42 现状 |
|---|---|---|
| 严重级重映射 | `-Werror=foo` 让单条 warning 变 error | ❌ |
| Warning 分组 | `-Wunused` / `-Wshadow` 等组级开关 | ❌ |
| Pragma 抑制 | `#pragma diagnostic ignored "Wxxx"` | ❌ |
| 参数化模板 | `diag(Err) << "got " << Type1 << " expected " << Type2` | ❌（消息是预格式化字符串） |

当前 35 个错误码（E01xx 到 E09xx）扁平组织。**短期 OK**，但 stdlib / 用户库规模长大后会扎手——库作者会要求"我能不能屏蔽这一类警告而不是逐个屏蔽"。

**建议**: M7 启动前不动，在 M7 期间规划一个 spec `diag-engine-v2`。最小落地：
- 给每条 diagnostic 加 `Group` 字段（`unused` / `shadow` / `convention` 等）
- 编译参数 `--no-warn=group` 抑制整组
- Pragma 抑制留到 trivia 系统（§2.5）就位后再做

### P2 — 长期观察

#### 3.4 无显式 CFG

[FlowAnalyzer.cs](../src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs) 直接对 `BoundBlock` 递归遍历做 `AlwaysReturns` 与 definite-assignment。Clang 有显式 `CFG` 数据结构（基本块 + 边），支撑 uninitialized-var、unreachable-code、thread-safety 等多种分析。

**当前 OK**：z42 的检查项目少，树遍历足够。
**何时该加**：当 z42 引入以下任一时——nullability flow（Option 类型的"何处一定 Some"）、闭包 escape 分析、loop-invariant 检测、liveness（用于寄存器分配优化）。届时再立 spec `introduce-cfg`。

#### 3.5 无 ASTContext 集中所有权

z42 用 `SymbolTable` + `Z42Type` 静态单例做局部对应；Clang 有单一 `ASTContext` 拥有所有 Type、Identifier 表、SourceManager。

**当前 OK**：z42 没有宏/include 链，单例 + 字典够用。
**何时该考虑**：暴露公共 API（§2.4）时，外部调用方需要一个明确的 "compilation context" 句柄，那时把分散的状态聚合成 `Z42CompilationContext` 是自然动作。

#### 3.6 Token.Text 是字符串副本

[Token.cs](../src/compiler/z42.Syntax/Lexer/Token.cs) 的 `Text` 是源文本的 string 拷贝。Clang 用源缓冲指针 + 偏移避免拷贝。10 万行+ 单文件才会感觉到，z42 远未到。

### Clang 视角下的 z42 正面设计（值得在 architecture 文档点名）

#### 3.7 错误恢复机制本身是对的，差最后一里

z42 用**不可变 `TokenCursor` + `ErrorStmt` / `ErrorExpr` 节点**，比 Clang panic-mode skip-to-sync 更优雅、更易测。基础设施已就位。

§2.2 的问题不是机制错，而是 `ParseException` 在声明边界吞了错误，**让现有恢复机制没被充分利用**。修起来比预想轻——把 try/catch 边界细化到语句级，把 `ErrorStmt` / `ErrorExpr` 节点真正用起来即可。这点应在 `parser-error-recovery` 的 design 中点名："基础已有，仅需启用"。

#### 3.8 Sema 不修改 AST

Clang Sema 边检查边重写 AST（插入 `ImplicitCastExpr` / 默认参数物化等）。z42 严格"AST 只读 → 产出 Bound 树"。

**优点**: AST 永远纯粹，可重复检查、可并行、可缓存（增量编译的基础）。
**缺点**: Codegen 必须自己处理隐式转换（[FunctionEmitter.cs](../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs) 在调用点做 cast IR 生成）。

权衡正确。Roslyn 在大规模代码上验证过该模型。**应在 [compiler-architecture.md](design/compiler-architecture.md) 显式记录"AST 不可变"作为不变量**，避免后人为了图方便破坏。

#### 3.9 Parse / Bind / Emit 严格分相

Clang 用 action-based parsing（Parser 边解析边调用 Sema callback）。z42 走完整三段。

**优点**: 测试性远胜——AST 可以是纯数据 fixture，Bind 可独立单元测试，Emit 同理。
**代价**: 多走一遍树（性能略差）。z42 体量下不重要。

### Clang vs z42 设计取舍补充表

| 维度 | z42 | Clang | 评价 |
|---|---|---|---|
| Decl 身份生命周期 | parse 后死亡 | 全程保留 | ❌ **该改**（§3.1） |
| 解释器/编译器调试 | `--dump-ast` 未实现 | `-ast-dump` 完整 | ⚠️ **该补**（§3.2） |
| 诊断引擎 | 扁平 + 静态 severity | 分组 + 重映射 + pragma | ⚠️ M7 期间规划（§3.3） |
| 流分析数据结构 | 树遍历 | 显式 CFG | ✅ 当前合理 |
| AST 上下文 | 分散 (SymbolTable + 单例) | ASTContext 单一拥有 | ✅ 当前合理 |
| AST 可变性 | 不可变（Sema 不改） | 可变（Sema 改写） | ✅ z42 更优 |
| 解析模型 | 三段分相 | action-based 单遍 | ✅ z42 更优 |
| 错误恢复机制 | 不可变 cursor + Error 节点 | panic-mode skip | ✅ z42 设计更优（§3.7） |

---

## Part 4 — VM 架构对标 (vs dotnet/runtime CoreCLR)

> **参考代码**: dotnet/runtime 在本机 `/Users/d.s.qiu/Documents/codesigner-ui/runtime/`（主要看 `src/coreclr/vm/`、`src/coreclr/gc/`、`src/coreclr/inc/corjit.h`）
>
> z42 VM 当前规模 ~17.5K LOC（4 个 crate），分相: `metadata` / `corelib` / `gc` / `interp` / `jit` / `native`。本部分按"对未来扩展的阻碍程度"排序。

### 概览

z42 VM **整体设计已具备小型 VM 的基本骨架**，并且有两个相对 CoreCLR 早期阶段就做对了的好选择:

- **[VmContext](../src/runtime/src/vm_context.rs) 集中化运行时状态**（445 LOC）— 替代 `thread_local!` 全局，让"多 VM 实例同进程共存"天然可行（embedding 友好）
- **`MagrGC` trait 已预留**（[gc/heap.rs](../src/runtime/src/gc/heap.rs)，10 capability groups，~30 method）— 比 CoreCLR `IGCHeap` 接口在 v1 时期还早形式化 GC 边界

但相对 CoreCLR 的成熟形态，有几条**会随 M7+ 与 L3 扩展线性放大成本**的设计裂缝。

### P0 — 影响 M7+ 与 L3 扩展性

#### 4.1 跨函数 / 跨类型引用用字符串名

[exec_instr.rs](../src/runtime/src/interp/exec_instr.rs) 中 `Call` / `CallVirtual` / `Builtin` 全部按 `qualified_fn_name: String` 在 HashMap 里查。

| 维度 | z42 | CoreCLR |
|---|---|---|
| 函数引用 | `String` + HashMap | metadata token → `MethodDesc*` 指针 |
| 类型引用 | `String` + HashMap | metadata token → `MethodTable*` 指针 |
| Builtin 分发 | `exec_builtin(name: &str, …)` | FCall MethodDesc，缓存原生指针 |

**实际成本**:
- 每次虚调用一次字符串哈希
- IR 体积膨胀（u32 vs 长字符串）
- `func_ref_cache_slots`（D1b 引入）已经在补救字符串查找的开销，是症状层修复

**建议**: 立 spec `introduce-method-token`——在加载期把所有跨引用解析成 `MethodId(u32)` / `TypeId(u32)`，IR 字段从 String 改 u32。**这个改造应当在 M7 启动前完成**，否则反射 R-series 落地后回头改的代价翻倍（reflection API 必须建立在稳定 token 系统上）。

#### 4.2 JIT/Interp 边界未形式化

CoreCLR 用 `ICorJitCompiler` + `ICorJitInfo`（~100 callback）严格隔离 JIT 与 EE。z42 当前:

- [jit/translate.rs](../src/runtime/src/jit/translate.rs)（833 LOC）里直接 `extern "C"` 声明了 **65 个 helper**
- 每加一个 IR opcode → 可能加一个 helper；helper 签名手写、无版本号
- `JitModuleCtx` 直接持 `*mut VmContext` 裸指针穿透所有抽象

**问题**: 当 M4 解释器全绿、真正实施 JIT 时，会面临"helper 集合稳定性"的痛点——helper 改签名就要联动改所有 codegen 与 Cranelift 注册。

**建议**: 在 M4 全绿之前（即 JIT 真正展开实施之前），立 spec `formalize-jit-vm-interface`，把当前 65 个 helper 收成一个 trait（或 `#[repr(C)]` vtable），定一个版本号字段。即使现在只有一个实现，**这条边界一旦形式化，后续 tier-up / ICorJitInfo 风格演进都顺**。

#### 4.3 元数据 eager full-load

[loader.rs](../src/runtime/src/metadata/loader.rs) 加载 .zbc 时一次性完整反序列化所有类型/函数。CoreCLR 用 RID maps 做**懒加载**——`MethodDesc` 在 chunk 内首次访问才物化。

**当前 OK**: z42 stdlib 还小。
**何时痛**: stdlib + 用户库共 50+ zpkg 时，VM 启动 = 全量解码所有 bincode。已有 [LazyLoader](../src/runtime/src/metadata/lazy_loader.rs) 但只对**模块整体**懒加载，模块内的 type/function 仍然是 eager。

**建议**: 不急于现在改，但在 [vm-architecture.md](design/vm-architecture.md) 显式记一笔"未来引入 RID-map 风格的 per-type 懒加载"，避免反射 R-series 设计时假设元数据全量在内存。

### P1 — 会随特性长大的问题

#### 4.4 异常模型与 JIT 集成脆弱

- 解释器：`ExecOutcome::Thrown(Value)` 通过返回值层层传上栈
- JIT：`pending_exception: Option<Value>` 在 VmContext，**每个 helper 调用后都要轮询**
- 异常表（`ExceptionEntry`）在每个函数本地，throw 时**线性扫描** try 区间

vs CoreCLR 两阶段 EH + funclet + RVA 索引的 EH clause table。

**问题**: JIT 侧"每次 call 后 polling pending_exception"在热路径上。当 try/catch 嵌套加深、性能基准跑起来，这条会出现在 profile 顶部。

**建议**: 落地 spec `eh-protocol-v2` —— 至少把异常表从线性扫描升级为按 try_start 排序 + 二分查找；JIT 侧引入"helper 直接 trap / unwind"机制（Cranelift 已支持 traps）。M7 期间规划。

#### 4.5 无统一 frame chain

z42 当前有**三种"frame"概念**:

| 来源 | 用途 |
|---|---|
| `interp::Frame` | 解释器活动记录（regs + env_arena） |
| `jit::JitFrame` | JIT 寄存器文件 + FRAME_POOL 缓存 |
| `VmContext::exec_stack` | GC 栈扫描用（`FrameGuard` RAII 推入） |

CoreCLR 用单一 `Thread::m_pFrame` 链表，EH / GC / profiler / debugger **共享**这一链。z42 的三套并存导致: stack trace 不可能（要做必须三处协调）；async/fiber 引入时三处都要同步改。

**建议**: 立 spec `unify-frame-chain`，引入轻量 `VmFrame { kind: Interp | Jit | Native, regs_ptr, prev: Option<NonNull<VmFrame>> }` 单链表，三套各持自己的扩展数据但共享 chain 节点。**Stack trace、profiler、debugger 都从这条链派生**。M7 内规划。

#### 4.6 无方法分类 / stub 机制

CoreCLR `MethodDesc` 有 `mcIL` / `mcFCall` / `mcPInvoke` / `mcArray` / `mcInstantiated` 分类，特殊方法直接挂原生 stub，**不走 IR 层**。

z42 当前所有方法都是 IR；native import 通过"合成空 ClassDecl + 特殊 Codegen"实现。Workable，但:
- Builtin 走 `Builtin` 指令 + `exec_builtin(name, …)` 字符串查找（每次调用一次哈希）
- Tier1 native 走 `CallNative` + libffi cif 重建

**建议**: 配合 §4.1 的 token 化，给 corelib builtin 在 load 时分配 `BuiltinId(u32)` 直接缓存函数指针，IR 改用 id。Tier1 native 同理缓存 cif。这是**无设计风险的纯优化**，可在 `introduce-method-token` 同 spec 内一并做。

### P2 — 长期 / 工具链

#### 4.7 无 tier-up 基础设施

`exec_mode` 是 IR 编译期注解（每个函数一个），不是**运行时 tier-up**。CoreCLR Tier 0 (interp) → Tier 1 (JIT) 由调用计数触发。

**何时该做**: M4 解释器 + JIT 都全绿稳定后，是自然的演进方向。需要的基础设施: 调用计数（每函数一个 `AtomicU32`）、热阈值、函数指针替换。**前置依赖 §4.1 token 化**——没有稳定 MethodId，无法做 tier-up 替换。

#### 4.8 无 ETW/EventPipe 风格诊断

`tracing` crate 仅做 VM 内部 log，没有用户可订阅的事件流（GC tick、JIT compiled、exception thrown）。Embedding 场景（z42 作为脚本宿主）会希望有这个。

**建议**: M7 之后立 spec `eventpipe-lite` —— 复用 `tracing::span` + `tracing::event`，定义一组稳定 event name + schema。低成本预留。

#### 4.9 单线程

已知限制。GC、VmContext、async/await 全部受此约束。L3 之前不动。多线程引入时需要的核心设计: GC suspension 协议、cooperative mode、线程本地 frame chain。

### CoreCLR 对照下值得点名的 z42 正面设计

写进 [vm-architecture.md](design/vm-architecture.md) 作为不变量，避免后续退化:

1. **`VmContext` 集中状态、零 thread_local 业务态**（除 JIT `FRAME_POOL` 分配缓存）— embedding / 多 VM 共存的基础
2. **Register VM**（vs CoreCLR stack VM 解释）— 一般性能更好
3. **`MagrGC` trait 已就位**（10 capability groups）— 比 CoreCLR 在 v1 时期还早形式化 GC 边界
4. **`PinView` 作为一等 `Value` 变体** — FFI 零拷贝模型干净，比 CoreCLR pinning + GCHandle 简洁
5. **静态字段在 `VmContext::static_fields`，不挂在 TypeDesc 上** — 多 VM 隔离天然成立
6. **GC stack scanning 用 `FrameGuard` RAII** — 比 CoreCLR 的 cooperative + Frame chain 更 Rust-ic，less ceremony

### CoreCLR vs z42 设计取舍补充表

| 维度 | z42 | CoreCLR | 评价 |
|---|---|---|---|
| 跨引用模型 | 字符串 + HashMap | metadata token → 指针 | ❌ **该改**（§4.1） |
| JIT/EE 边界 | 65 个 extern helper 直裸 | `ICorJitCompiler` + `ICorJitInfo` | ❌ **该形式化**（§4.2） |
| 元数据加载 | eager full-load | RID-map 懒加载 | ⚠️ M7 后规划（§4.3） |
| 异常分发 | 线性扫表 + value 传播 | 两阶段 EH + funclet + RVA 索引 | ⚠️ M7 期间规划（§4.4） |
| 栈遍历 | 三套 frame 概念并存 | 单一 `Thread::m_pFrame` 链 | ⚠️ **该统一**（§4.5） |
| 方法分类 | 全部走 IR | mcIL / mcFCall / mcPInvoke 分类 stub | ⚠️ 与 §4.1 同 spec（§4.6） |
| 运行时状态归属 | `VmContext` 集中 | `Thread` TLS + AppDomain | ✅ z42 更优 |
| GC 接口 | `MagrGC` trait 已就位 | `IGCHeap` 接口 | ✅ 一致，方向对 |
| FFI 模型 | `PinView` first-class Value | GCHandle + pinning | ✅ z42 更简洁 |
| 静态字段 | 在 VmContext | 在 MethodTable | ✅ z42 更优（多 VM 隔离） |
| Tier-up | 无（编译期 exec_mode） | Tier 0 → Tier 1 调用计数 | ⚠️ L3 准备（§4.7） |
| 诊断 | tracing 内部 log | ETW + Profiler API | ⚠️ M7 后规划（§4.8） |
| 多线程 | 单线程 | 多线程 + 协作式 GC | ⚠️ L3 准备（§4.9） |

---

## 推进路线图

按"对未来工作的阻塞程度"排序。每项标注变更类型（`refactor` / `lang` / `docs`）便于 spec 立项。

**编译器**（Compiler）和 **VM**（Runtime）两条线分开排序，可并行推进:

#### 编译器线

| 时机 | 工作 | 类型 | 影响 | 状态 |
|---|---|---|---|---|
| **现在**（M6 收尾） | 完成 `extend-signature-whitelist` | 进行中 | 当前 spec | 🟡 |
| **本迭代结束前** | `spec/changes/` 两个目录纳入 git | (随提交) | 防止脱管 | 📋 |
| **M6 → M7 之间** | `introduce-bound-visitor` — 引入 `BoundExprVisitor<T>` / `BoundStmtVisitor<T>`，迁 `FunctionEmitter*` 与 `TypeChecker.Exprs` | refactor | Part 1 §1.1、§1.3 + Part 2 §2.1；为 §3.2 dump-ast 提供基础 | 📋 |
| **M6 → M7 之间** | `parser-error-recovery` — 启用现有 ErrorStmt/ErrorExpr 节点，多错聚合 | refactor | Part 2 §2.2 + Part 3 §3.7，LSP 前置 | 📋 |
| **M6 → M7 之间** | `split-large-codegen-files` — IrGen、ImportedSymbolLoader、TypeChecker.Calls | refactor | Part 1 §1.1 残留 | 📋 |
| **M6 → M7 之间** | `split-large-test-files` — TypeCheckerTests / rc_heap_tests | refactor | Part 1 §1.4 | 📋 |
| **M6 → M7 之间** | `impl-dump-ast` — 实现 `--dump-ast` handler（依赖 `introduce-bound-visitor`） | refactor | Part 3 §3.2 | 📋 |
| **M7 启动前**（**关键前置**） | `split-symbol-from-type` — 抽 `ISymbol` 层；**Symbol 持有 `DeclSpan`，根除 §3.1 Decl 身份丢失** | lang | Part 2 §2.3 + Part 3 §3.1，R-series 反射前置 | 📋 |
| **M7 期间** | `lexer-trivia-preserve` — lexer 加 trivia 字段（可空） | refactor | Part 2 §2.5，formatter 前置 | 📋 |
| **M7 期间** | `diag-engine-v2` — warning groups + severity 重映射 | lang | Part 3 §3.3 | 📋 |
| **M7 之后** | 公共 API 边界声明（仅文档 + 命名空间约定） | docs | Part 2 §2.4 + Part 3 §3.5 | 📋 |
| **M7 之后** | 在 [compiler-architecture.md](design/compiler-architecture.md) 记录"AST 不可变 / 三段分相"作为不变量 | docs | Part 3 §3.8、§3.9 防退化 | 📋 |
| **L3 之后** | 评估 native 签名解析统一；评估引入 CFG | 待评估 | Part 2 §2.6 + Part 3 §3.4 | 📋 |

#### VM 线（Rust runtime）

| 时机 | 工作 | 类型 | 影响 | 状态 |
|---|---|---|---|---|
| **M4 全绿前** | `formalize-jit-vm-interface` — 把 65 个 extern helper 收成 trait/vtable，定版本号 | refactor | Part 4 §4.2，JIT 真正展开前必须 | 📋 |
| **M6 → M7 之间** | `split-exec-instr` — `exec_instr.rs` (756 LOC) 按 op 类别拆分 | refactor | Part 1 §1.2 | 📋 |
| **M7 启动前**（**关键前置**） | `introduce-method-token` — String → `MethodId` / `TypeId` / `BuiltinId`，IR 字段 u32 化（含 §4.6 builtin/native 缓存） | lang+vm | Part 4 §4.1 + §4.6，反射 R-series 前置 | 📋 |
| **M7 期间** | `unify-frame-chain` — 单一 `VmFrame` 链表，统一 interp / jit / GC scan | refactor | Part 4 §4.5，stack trace / debugger 前置 | 📋 |
| **M7 期间** | `eh-protocol-v2` — 异常表二分查找 + JIT trap 集成 | vm | Part 4 §4.4，依赖 `unify-frame-chain` | 📋 |
| **M7 之后** | 在 [vm-architecture.md](design/vm-architecture.md) 记录"VmContext 集中 / Register VM / PinView first-class / static-in-context"作为不变量 | docs | Part 4 §4.正面设计 防退化 | 📋 |
| **M7 之后** | `lazy-metadata-loading` — RID-map 风格 per-type 懒加载 | vm | Part 4 §4.3，依赖 `introduce-method-token` | 📋 |
| **M7 之后** | `eventpipe-lite` — tracing-based 用户可订阅事件流 | vm | Part 4 §4.8，embedding 友好 | 📋 |
| **L3 准备** | tier-up 基础设施 | vm | Part 4 §4.7，依赖 `introduce-method-token` + `formalize-jit-vm-interface` | 📋 |
| **L3 准备** | 多线程 GC suspension 协议（大件） | vm | Part 4 §4.9 | 📋 |

### 关键依赖关系

```
extend-signature-whitelist (进行中)
    ↓
┌─ 编译器线 ─────────────────┐  ┌─ VM 线 ────────────────────┐
│                            │  │                            │
│ introduce-bound-visitor ──┐│  │ formalize-jit-vm-interface │
│ parser-error-recovery   ──┤│  │ split-exec-instr           │
│ split-large-codegen-files─┤│  │                            │
│ split-large-test-files  ──┤│  │           ↓                │
│           ↓                │  │ introduce-method-token  ←───┼─── M7 启动前必须
│ impl-dump-ast              │  │           ↓                │     （含 builtin/native 缓存）
│           ↓                │  │ unify-frame-chain          │
│ split-symbol-from-type ←───┼──┤           ↓                │
│   (含 Decl 身份保留)       │  │ eh-protocol-v2             │
└─────────────┬──────────────┘  └─────────────┬──────────────┘
              ↓                                ↓
           M7 (VM metadata + reflection R-series + tier-up 准备)
              ↓
       lexer-trivia / diag-engine-v2 / lazy-metadata / eventpipe-lite
              ↓
         L3 (tier-up / 多线程 / 公共 API)
```

### 立项建议优先级

**最高优先**（M7 启动前必须）:

1. `split-symbol-from-type` — 反射系列的设计前置；同步根除 Decl 身份丢失（§3.1）
2. `introduce-method-token` — VM 反射 R-series 前置；同步处理 builtin / native 缓存（§4.1+§4.6）
3. `introduce-bound-visitor` — 阻止 `FunctionEmitter*` 继续膨胀；为 dump-ast 提供基础
4. `parser-error-recovery` — IDE/LSP 前置；现有 ErrorStmt/ErrorExpr 基础已有，仅需启用
5. `formalize-jit-vm-interface` — JIT 真正展开前必须形式化（§4.2）

**高优先**（M7 启动前应完成）:

6. `split-large-codegen-files` + `split-large-test-files` — 直接消除当前 [code-organization.md](../.claude/rules/code-organization.md) 违规
7. `split-exec-instr` — Rust 端同款问题
8. `impl-dump-ast` — 开发期高频价值，依赖 visitor 框架后近乎免费

**中优先**（M7 内或之后）:

9. `unify-frame-chain` — stack trace / debugger 前置（§4.5）
10. `eh-protocol-v2` — 异常分发性能 + JIT 集成（§4.4）
11. `lexer-trivia-preserve` — 单向门，越晚改成本越高
12. `diag-engine-v2` — warning groups + severity 重映射
13. 在 [compiler-architecture.md](design/compiler-architecture.md) / [vm-architecture.md](design/vm-architecture.md) 记录正面设计作为不变量
14. 公共 API 边界声明 — 仅文档工作

**低优先**（观察）:

15. `lazy-metadata-loading` / `eventpipe-lite` — M7 之后再评估
16. native 签名解析统一 — 等 M7 反射落地后再评估
17. 引入显式 CFG — 当 nullability/escape/liveness 分析需要时
18. tier-up + 多线程 — L3 准备阶段大件

---

## 后续动作（即可执行）

1. **本次提交**: 把 `spec/changes/add-array-base-class/`、`spec/changes/extend-signature-whitelist/` 与本审查报告一并 commit（按 [workflow.md](../.claude/rules/workflow.md) 阶段 9 自动提交规则）
2. **下一会话**: 完成 `extend-signature-whitelist` 实施；同时草拟 `introduce-bound-visitor` 的 proposal（`refactor` 类型，最小化模式即可）
3. **M7 准备阶段**: 完成 `split-symbol-from-type` 的 proposal + design 起草（`lang` 类型，需 User 审批）

---

## 附录: 不在本报告范围内的考察

以下方向被有意排除，未来需要时单独立项：

- **性能基准**: 编译速度、内存占用、增量构建命中率（无现成 benchmark 数据）
- **VM 性能**: GC 停顿、解释器吞吐量（M4 阶段单独评估）
- **依赖审计**: NuGet / Cargo 第三方依赖的版本与许可证（CI 工作）
- **跨平台**: Windows / Linux 适配（CLAUDE.md 当前 darwin only）
- **stdlib 内容覆盖度**: 标准库 API 完整度（M7 范围）

## 修订记录

- **2026-05-05 初版**: Part 1（工程组织）+ Part 2（Roslyn 视角）
- **2026-05-05 增补 Part 3**: Clang 视角对标
- **2026-05-05 增补 Part 4**: VM 架构对标 (vs dotnet/runtime CoreCLR)；路线图拆为编译器线 + VM 线两轨
