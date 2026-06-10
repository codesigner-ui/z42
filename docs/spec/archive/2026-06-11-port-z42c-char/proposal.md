# Proposal: port-z42c-char — char 字面量前端整链

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（instance-import 归档后接力）

## Why

char 是 zbc-writer 期（ZW-1E）就挂账的缺口：z42c 前端完全无 char 字面量（lexer 不识 `'a'`），导致含 char 的源无法自举编译——stdlib 大量用 char（CharAt 返回值比较 `c == '.'` 是 z42c 自身源码的高频写法，**编译 z42c 自身**绕不过它。

## What Changes（lexer→parser→typecheck→codegen→writer 整链）

- **CH-1 syntax**：Lexer char 字面量规则（`'X'`/`'\n'` 等转义；token kind charlit）+ `CharLitExpr`（解码后 char 值 + Span）+ Parser 字面量分支（镜像 C# UnescapeChar：剥引号+复用串转义解码）
- **CH-2 semantics**：`BoundLitChar`（Z42PrimType("char")）+ TypeChecker 绑定 + BinaryTypeTable char 比较放行（char 可 ==/!=/</> 与 char——按 C# 现状校准）
- **CH-3 ir/codegen**：`ConstCharInstr`（dump `const.char 'X'`）+ ExprEmitter 字面量分支 + ZbcInstr 编码（**op 0x08 + tag(FromIrType) + dst u16 + i32 值**，C# 实证）+ REGT walk visit 补 ConstChar
- **CH-4 验证**：zbc e2e 第 4 源 charcheck（char 声明/比较/`(int)c` 算术 + div-zero oracle）→ z42vm 执行 + **zbc byte-compare 升 4/4**；单测（lexer/parser/typecheck/codegen 各 ≥1）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.syntax/src/Lexer.z42` | MODIFY | char 字面量规则 |
| `src/z42c/z42c.syntax/src/Ast.z42` | MODIFY | CharLitExpr |
| `src/z42c/z42c.syntax/src/Parser.z42` | MODIFY | 字面量分支 + 解码 |
| `src/z42c/z42c.syntax/tests/lexer/lexer_tests.z42` | MODIFY | char token 单测 |
| `src/z42c/z42c.syntax/tests/parser/parser_tests.z42` | MODIFY | CharLit dump 单测 |
| `src/z42c/z42c.semantics/src/Bound.z42` | MODIFY | BoundLitChar |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | 绑定 + char 比较 |
| `src/z42c/z42c.semantics/src/TypeFacts.z42`（或 BinaryTypeTable）| MODIFY | char 运算规则（按 C# 校准）|
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | ConstChar 发射 |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.ir/src/IrInstr.z42` | MODIFY | ConstCharInstr |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcInstr.z42` | MODIFY | 编码 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` | MODIFY | Op.ConstChar=0x08 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | REGT walk visit |
| `scripts/xtask_compiler_z42.z42` | MODIFY | charcheck 第 4 zbc 源 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 缺口销账 |

**只读引用**：C# `TokenDefs.cs`（CharLiteral 规则）/`ExprParser.Atoms.cs`（UnescapeChar）/`ZbcWriter.Instructions.cs`（编码）/`BinaryTypeTable.cs`（char 运算面）。

## Out of Scope
- `'A'` Unicode 转义（z42c DecodeString 既有延后项，跟随）、char↔int 隐式宽化全表（按 corpus 实证最小集）、字符串索引 `s[i]`→char

## Open Questions
- [ ] Q1：char 与 int 的比较/算术是否隐式宽化按 C# BinaryTypeTable 实测——校准制
