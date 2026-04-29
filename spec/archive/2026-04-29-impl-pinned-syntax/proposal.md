# Proposal: `pinned` block syntax (C5)

## Why

C4 让 VM runtime 支持 `Value::PinnedView` + `PinPtr`/`UnpinPtr` opcode，但 z42 用户**仍写不出** `pinned p = s { ... }` —— 编译器没接通 syntax → IR codegen。这意味着 C2/C3/C4 的 native interop 接口骨架在 z42 源代码层面**完全不可用**。

C5 把 `pinned` 块语法从 lexer → parser → typecheck → IR codegen 五个阶段全链路接通：

```z42
fn read_length(s: string) : long {
    pinned p = s {
        let n : long = p.len;
        return n;
    }
}
```

> `[Native(lib=..., entry=...)]` extended attribute、`extern class T { ... }`、`import T from "lib"`、manifest reader 等其他 user-facing FFI 语法**不在 C5 范围**——本 spec 仅做 `pinned` 块。其他语法在后续 spec 增量引入，避免一次改动横跨编译器太多模块。

## What Changes

- **Lexer**：新增 `pinned` 关键字（`TokenKind.Pinned`，phase = L2）
- **AST**：新增 `PinnedStmt(Identifier Name, Expr Source, BlockStmt Body, Span Span) : Stmt` `sealed record`
- **Parser**：在 `StmtParser.s_table` 注册 `Pinned` → `ParsePinned`；语法形式 `pinned <ident> = <expr> { <stmts...> }`
- **TypeChecker**：
  - 校验 source 表达式类型为 `string` （C5 唯一支持类型；`byte[]` 留待后续 spec 引入字节缓冲后启用）
  - 在 body scope 引入 `name` 局部，type = 内置类型 `PinnedView`
  - PinnedView 仅支持 `.ptr` / `.len` 字段访问（均返回 `long`）；其他用法报错
  - 块内 source 局部不可重新赋值（重赋值报 Z0908）
  - 块内禁用 `return` / `break` / `continue` / `throw`（C5 限制；UnpinPtr emit 简化保平直控制流）—— 报 Z0908
- **IR Codegen**：
  - 进入块前 emit `PinPtr <view_reg>, <source_reg>`
  - 编译 body 内语句
  - 块退出 emit `UnpinPtr <view_reg>`
  - body 内 `view.ptr` / `view.len` → 现有 `FieldGet` IR opcode（C4 runtime 已支持）
- **Z0908 扩展抛出条件**（在 TypeChecker 阶段）：
  - source 类型不可 pin
  - 块内修改 source
  - 块内含 `return` / `break` / `continue` / `throw`
- **Tests**：5 个 C# 编译器单测 + 1 个 golden e2e（`pinned_basic.z42`）
- **文档同步**：`grammar.peg` / `language-overview.md` / `interop.md` / `roadmap.md`

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY | 加 `Pinned` enum 项 |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | `KeywordDefs` 加 `pinned` (L2 phase) |
| `src/compiler/z42.IR/AST/Stmts.cs` 或同等 | MODIFY | 加 `PinnedStmt` record（具体路径由 grep 决定） |
| `src/compiler/z42.Syntax/Parser/StmtParser.cs` | MODIFY | `s_table` 注册 + `ParsePinned` 实现 + `SkipToNextStmt` 加 Pinned 入口 |
| `src/compiler/z42.Semantics/TypeCheck/StmtCheck.cs` 或同等 | MODIFY | `PinnedStmt` 类型规则 + scope 引入 + 控制流限制 |
| `src/compiler/z42.Semantics/TypeCheck/Types/Builtins.cs` 或同等 | MODIFY | 注册 `PinnedView` 内置类型 + 字段表 |
| `src/compiler/z42.Semantics/Codegen/StmtCompiler.cs` 或同等 | MODIFY | `PinnedStmt` → PinPtr/Body/UnpinPtr IR |
| `src/compiler/z42.Tests/PinnedSyntaxTests.cs` | NEW | 5 个 C# 单测：lexer / parser / typecheck / codegen / 错误 |
| `tests/golden/run/pinned_basic/source.z42` | NEW | 端到端示例：`pinned p = s { return p.len; }` |
| `tests/golden/run/pinned_basic/expected.txt` | NEW | 期望输出 (`5`) |
| `examples/pinned_basic.z42` | NEW | 用户示例 |
| `docs/design/grammar.peg` | MODIFY | 加 pinned-stmt 产生式 |
| `docs/design/language-overview.md` | MODIFY | 加 pinned 语法描述 |
| `docs/design/interop.md` | MODIFY | §6.3 / §10 C5 → ✅ |
| `docs/roadmap.md` | MODIFY | C5 → ✅ |
| `docs/design/error-codes.md` | MODIFY | Z0908 抛出条件加 TypeChecker 三项 |

**只读引用**：
- `src/compiler/z42.IR/IrModule.cs` — `PinPtrInstr` / `UnpinPtrInstr` IR record（C1 已声明）
- `src/runtime/src/interp/exec_instr.rs::PinPtr/UnpinPtr/FieldGet` — VM runtime（C4 ready）
- `src/compiler/z42.Syntax/Parser/StmtParser.cs` — 对照其他 stmt 实现风格（`ParseTryCatch` 等）

## Out of Scope

- `[Native(lib=..., entry=...)]` 扩展 attribute / `extern class T { ... }` / `import T from "lib"` —— 后续 spec
- Manifest reader (`.z42abi`) —— 后续 spec
- `byte[]` / `Array<u8>` pin 支持 —— 等待 byte buffer Value variant 引入后再做
- 块内 `return` / `break` / `throw` 自动 unpin —— 当前限制；如需放开，未来引入 try-finally-like emit
- 嵌套 pinned 块 —— C5 允许但不专门测试
- JIT emit PinPtr/UnpinPtr —— L3.M16

## Open Questions

- [ ] **Q1**：PinnedView 是 builtin "类型" 还是仅 syntax-time 名字？
  - 倾向：**syntax-time 名字 + TypeChecker 内嵌字段映射**（`.ptr` / `.len` → `long`）。不引入 stdlib type，避免 IR / VM 端类型注册的额外工作；与 string.Length 内置字段同样模式
- [ ] **Q2**：禁止 return / break / throw 是否过严？
  - 倾向：**C5 范围内严格禁止**，TypeChecker 报清晰错误指向 spec。后续 spec 评估放开（需要 IR 层 try-finally-like UnpinPtr emission）
- [ ] **Q3**：pinned 变量是否可在 body 中重新赋值？
  - 倾向：禁止。`pinned p = s { p = ...; }` 报 Z0908；同时 `s` 也禁止赋值
