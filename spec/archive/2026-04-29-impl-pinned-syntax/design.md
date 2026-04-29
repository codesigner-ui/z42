# Design: `pinned` block syntax (C5)

## Architecture

```
Source: `pinned p = s { let n = p.len; ... }`

Lexer:
  pinned IDENT = EXPR { ... }
  ───────^^^^^^^^^^^^^^^^^^^^^
   PinnedKeyword + Identifier + Eq + Expr + LBrace ...

Parser → AST:
  PinnedStmt(name = "p", source = NameExpr("s"), body = BlockStmt[...], span)

TypeChecker:
  - source.Type ∈ {string}     → ok      else Z0908_NotPinnable
  - body 内 source 不可重赋值              else Z0908_PinnedSourceMutated
  - body 不含 return/break/continue/throw  else Z0908_PinnedControlFlow
  - body 内 `p` 可见，类型 = PinnedView (虚拟内置)
  - `p.ptr` / `p.len` → long；其他字段 → 普通字段访问报错

IR Codegen:
  PinPtr <p_view>, <s_reg>
  ... body IR (含 FieldGet on p_view 走 C4 runtime) ...
  UnpinPtr <p_view>

VM (C4 已就绪):
  PinPtr → Value::PinnedView { ptr, len, kind=Str }
  FieldGet → Value::I64(ptr) / Value::I64(len)
  UnpinPtr → no-op (RC backend)
```

## Decisions

### Decision 1: `PinnedView` 不是真实 stdlib 类型

为避免 IR 层 / VM 层重复登记内置类型，`PinnedView` 仅在 TypeChecker 内部作为虚拟 type 名字（与 `string.Length` 内置字段同模式）。`p.ptr` / `p.len` 在 TypeChecker 直接特判返回 `long`；不进 stdlib BCL。

### Decision 2: 块内控制流 strict (C5 限制)

禁止 `return` / `break` / `continue` / `throw`。理由：
- IR codegen 简单：进/出块各 emit 一次 PinPtr/UnpinPtr，不需要在每个 exit point 插 UnpinPtr
- 用户拿到清晰错误信息（指向 spec）
- 放开需要 try-finally-like emission，留给独立 spec

### Decision 3: 词法 + Token

`TokenKind.Pinned` 新枚举值；`KeywordDefs` 加一行 `new("pinned", TokenKind.Pinned, LanguagePhase.Phase2)`。位置紧接 `Try` / `Throw` 之类的 Phase2 关键字。

### Decision 4: AST 节点

```csharp
public sealed record PinnedStmt(
    string Name,           // 局部变量名
    Expr Source,           // 被 pin 的表达式
    BlockStmt Body,        // 块体
    Span Span
) : Stmt;
```

放在 `Stmts.cs` 里其他 stmt（VarDeclStmt / TryCatchStmt 等）旁。

### Decision 5: Parse 形式

```
pinned <ident> = <expr> { <stmts> }
```

`ParsePinned` 流程：
```csharp
var name = Expect(ref cursor, TokenKind.Identifier).Text;
Expect(ref cursor, TokenKind.Eq);
var source = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
return ParseResult<Stmt>.Ok(new PinnedStmt(name, source, body, kw.Span), cursor);
```

`feat.IsEnabled(LanguageFeature.NativeInterop)` —— 保留接口；C5 默认启用 phase2。如果没有 feature 枚举条目则不加 gate。

### Decision 6: TypeChecker 流程

伪代码：
```csharp
case PinnedStmt p:
    var srcType = TypeCheck(p.Source);
    if (srcType != BuiltinTypes.String)
        diag.Error(Z0908, "Z0908: source of `pinned` must be string (current C5 limitation; Array<u8> arrives in a follow-up spec)", p.Source.Span);

    using (var scope = env.PushScope()) {
        // p 在 body 内可见，type = "PinnedView" 哨兵
        scope.Declare(p.Name, BuiltinTypes.PinnedView);
        scope.MarkSourceAsPinned(p.Source.AsLocalName());  // 用于 mutation 检查

        scope.MarkInPinnedBlock();  // 控制流检查

        TypeCheck(p.Body);  // 进入块体；FieldAccess 检查时识别 PinnedView 走 .ptr/.len 内联
    }
```

`MarkInPinnedBlock` 让 ReturnStmt/BreakStmt/ContinueStmt/ThrowStmt 在 TypeCheck 时报 Z0908。

`MarkSourceAsPinned` 让 AssignExpr 在 TypeCheck 时检测对该 local 的赋值并报 Z0908。

> **简化**：如果 source 不是简单 `NameExpr`（即 `pinned p = computed_expr() { ... }`），TypeChecker 自动接受（无 source local 可冻结），但 PinnedView 与 source 表达式的 lifetime 由编译器引入的 hidden temp 保证（IR 端引入 hidden reg）。

### Decision 7: IR Codegen

```csharp
case PinnedStmt p:
    var srcReg = CompileExpr(p.Source);
    var viewReg = AllocReg(IrType.Ref);  // PinnedView 在 IR 层就是 Ref
    Emit(new PinPtrInstr(viewReg, srcReg));
    
    // body 内引用 p.Name → viewReg
    locals[p.Name] = viewReg;
    CompileBlock(p.Body);
    
    Emit(new UnpinPtrInstr(viewReg));
```

`p.ptr` / `p.len` 编译到 `FieldGetInstr(dst, viewReg, "ptr"/"len")`，VM 端 C4 已实现。

### Decision 8: 测试结构

5 个单元 + 1 个 golden：

| Test | 验证 |
|------|------|
| `Lexer_Pinned_Tokenizes` | `pinned` → TokenKind.Pinned |
| `Parser_Pinned_BasicForm` | `pinned p = s { ... }` → PinnedStmt AST 正确 |
| `Parser_Pinned_MissingBlock_Errors` | 缺 `{` 时 ParseException |
| `TypeCheck_Pinned_NonStringSource_Z0908` | `pinned p = 42 { ... }` → Z0908 |
| `TypeCheck_Pinned_ReturnInBody_Z0908` | 块内含 `return` → Z0908 |
| `Codegen_Pinned_EmitsPinPtrUnpinPtr` (golden) | `pinned_basic.z42` 编译运行 → 期望输出 |

## Risk & Rollback

- **风险 1**：TypeChecker 现有 scope / control-flow 跟踪机制可能没有 hook 点；MarkInPinnedBlock 实现细节因 codebase 而异
  - 缓解：先读现有 BreakStmt / ContinueStmt 校验逻辑，复用同一 mechanism；如完全没有，引入最小 stack flag
- **风险 2**：`pinned` 关键字与现有标识符冲突（grep 全仓 `pinned`）
  - 缓解：实施前 grep 确认零冲突
- **回滚**：单 commit 即可整体 revert；C4 runtime 不动，CLI / 其他特性零影响
