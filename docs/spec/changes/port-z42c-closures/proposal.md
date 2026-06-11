# Proposal: port-z42c-closures — 闭包/lambda 整链（三大件第③·最后一件）

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（interface 归档后接力）

## Why

三大件收官。z42c 完全无 lambda：parser 不识 `=>`（1E 期挂账）、无捕获分析、无 LoadFn/MkClos/CallIndirect 三指令。stdlib API 大量 Action/Func 形参——没有闭包，z42c 编译面永远缺一角。C# 全链已 survey（四指令字节编码/lift 命名/env 重写/FRCS）。

## What Changes（MVP：lambda 字面量赋 Func 变量 + 间接调用；方法组/LoadFnCached/FRCS 延后）

- **CL-1 syntax**：lambda 解析（`x => expr` / `(a, b) => { ... }`；LambdaExpr/LambdaParam/双体形态；FatArrow lexer 已有则复用）
- **CL-2 typecheck**：BindLambda（expectedFn 自 var-decl/assign 目标类型推导；参数类型显式或继承 expected）+ **Func/Action/Predicate 名→结构 Z42FuncType**（ResolveTypeP；C# 经委托注册表但结果同构）+ **捕获分析**（lambda frame：外层命中 → BoundCapture 记录 + 体内引用标记）+ Func 值调用 → BoundIndirectCall
- **CL-3 codegen**：lift（命名 `{enclFQ}__lambda_{idx}`，**模块函数表末尾追加**）；无捕获 → LoadFn；有捕获 → env 注入 reg0 + 体内捕获引用→ArrayGet env[idx] + MkClos(captures, stackAlloc=false——逃逸分析延后恒堆)；BoundIndirectCall → CallIndirect
- **CL-4 writer**：LoadFn 0x55 / CallIndirect 0x56 / MkClos 0x57 编码（survey 字节布局）+ REGT visits + intern（lift 名经 ResolveMethod 既有 token 路径）
- **CL-5 验证**：closcheck 第 7 zbc 源（无捕获 dbl + 捕获 mul + 间接调用 + oracle）→ 执行 + **byte-compare 7/7**；单测 ≥4（parser/typecheck 推导/捕获/codegen dump）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.syntax/src/Ast.z42` | MODIFY | LambdaExpr/LambdaParam |
| `src/z42c/z42c.syntax/src/Lexer.z42` | MODIFY | （如缺）FatArrow |
| `src/z42c/z42c.syntax/src/Parser.z42` | MODIFY | lambda 检测+解析 |
| `src/z42c/z42c.syntax/tests/parser/parser_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/src/Z42Type.z42` | MODIFY | （如需）FuncType 辅助 |
| `src/z42c/z42c.semantics/src/SymbolTable.z42` | MODIFY | Func/Action/Predicate 结构解析 |
| `src/z42c/z42c.semantics/src/Bound.z42` | MODIFY | BoundLambda/BoundCapture/BoundCapturedIdent/BoundIndirectCall |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | BindLambda+捕获+间接调用 |
| `src/z42c/z42c.semantics/src/TypeEnv.z42` | MODIFY | lambda frame 标记 |
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | MODIFY | lift 发射 |
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | LoadFn/MkClos/CallIndirect/env 引用 |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | lambda 计数器/lift 收集 |
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | lift 函数末尾追加 |
| `src/z42c/z42c.ir/src/IrInstr.z42` | MODIFY | 三指令 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` | MODIFY | opcodes |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcInstr.z42` | MODIFY | 编码 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | REGT visits |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | 单测 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | closcheck 第 7 源 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 三大件收官同步 |

**只读引用**：C# ExprParser.Atoms.cs(ParseLambda)/TypeChecker.Exprs.cs(BindLambda)/FunctionEmitterExprs.Lambdas.cs/ZbcWriter.Instructions.cs(115-135)/survey 报告全文。

## Out of Scope
- 方法组转换（LoadFnCached/FRCS 段）、逃逸分析（stackAlloc 恒 false=堆）、嵌套捕获 lambda（env 链 ArrayGet 嵌套）、lambda 作实参直接传（仅 var-decl/assign 目标推导）、ReferenceShare 语义差异验证

## Open Questions
- [ ] Q1：BoundCapture 体内引用的标记形态（typecheck 标 vs codegen 查 captures 表）按实施最简定，字节以 lift 函数体对账校准
