# Design: 嵌套 delegate dotted-path 类型访问（D-6）

## Architecture

```
Source: Btn.OnClick handler;

Lexer:        Ident("Btn")  Dot  Ident("OnClick")  Ident("handler")  Semicolon
   │
   ▼
TypeParser:   parse NamedType("Btn") → lookahead `.` → consume → parse Ident("OnClick")
              → wrap MemberType(NamedType("Btn"), "OnClick", span)
   │
   ▼
SymbolTable.ResolveType(mt: MemberType):
              left = ResolveType(mt.Left)  // → Z42ClassType("Btn")
              if (left is ClassType ct):
                  if (ct.NestedDelegates.TryGetValue(mt.Right, out di)):
                      return di.Signature
                  else:
                      ERROR E0401 "no nested type Right on Left"
              else:
                  ERROR "cannot access nested type on non-class"
   │
   ▼
TypeChecker / IrGen / VM 路径与 NamedType resolve 出的 delegate 共用
```

## Decisions

### Decision 1：MemberType 是新 AST 节点，不是 NamedType 的字符串拼接

**问题**：`Btn.OnClick` 在 AST 中如何表示？

**选项**：
- A — 复用 NamedType，Name 字段用 `"Btn.OnClick"`（点号在字符串里）
- B — 新增 `MemberType(Left: TypeExpr, Right: string)` 节点

**决定**：**B**

**理由**：
- A 让 Name 字段语义模糊（既是 simple name 又是 dotted），符号表 lookup 要 split + try simple → try qualified，多分支
- B 结构清晰：Left 是 TypeExpr 可递归，Right 是 nested 名；ResolveType 直接走 MemberType 分支
- A 还会让现有所有处理 NamedType 的代码隐式接受 dotted 字符串（无意中可能匹配错），B 强类型安全
- B 未来扩展 namespace dotted-path / static member type access 时无需改 NamedType

### Decision 2：MemberType 解析左结合，递归 wrap

**问题**：`A.B.C` 该 parse 成 `MemberType(MemberType(A, B), C)` 还是 `MemberType(A, MemberType(B, C))`？

**决定**：**左结合** `MemberType(MemberType(A, B), C)`

**理由**：
- 与 expression-level member access (`a.b.c`) 解析方向一致，认知一致
- ResolveType 按"先解 Left 拿 owner，再 lookup Right"自然展开，左结合天然适配
- 右结合需要先 lookup `B.C`（无意义）再去 `A` 找

### Decision 3：本变更只支持 1 层嵌套（class 内的 nested delegate）

**问题**：嵌套 delegate 当前只在 1 层（class 内），如果用户写 `A.B.C` 但 SymbolCollector 只注册 1 层 qualified key 怎么办？

**决定**：parser 接受任意深度（保留未来扩展），ResolveType 在第 2 层 lookup miss 时报清晰错误：``nested type `B.C` not found; only one-level nesting (Class.Delegate) is supported``

**理由**：
- parser 不限制深度，避免未来扩展时还要改 parser
- ResolveType 给清晰错误，用户立即知道用 top-level 声明替代
- 不偷偷支持，避免半成品行为

### Decision 4：泛型 nested delegate 暂不支持，但 parser 接受 generic syntax

**问题**：`Btn.OnClick<int>`（嵌套 generic delegate）该不该支持？

**决定**：parser 接受（`MemberType(Left, Right)` + Right 后跟 `<...>` → `GenericType(MemberType(...), TypeArgs)` 包装一层），但 ResolveType 在 SymbolCollector 没注册 nested generic delegate 时报清晰错误

**理由**：
- 当前 nested delegate 都是非泛型（D1a 决策），泛型 nested 是边缘场景
- parser 兼容生态，未来 SymbolCollector 扩展即可激活
- 给清晰错误避免静默失败

## Implementation Notes

### AST 节点

```csharp
// Ast.cs
public sealed record MemberType(TypeExpr Left, string Right, Span Span) : TypeExpr;
```

### TypeParser dotted-path lookahead

伪代码（实际位置看 TypeParser.cs 当前 NamedType 分支）：

```csharp
TypeExpr ParseTypeCore(...) {
    var head = ParseNamedOrGeneric(...);  // 现有 NamedType / GenericType
    while (cursor.Peek() == TokenKind.Dot) {
        cursor = cursor.Advance();
        var rightTok = cursor.Expect(TokenKind.Ident);
        var span = SpanUnion(head.Span, rightTok.Span);
        head = new MemberType(head, rightTok.Text, span);
        // 处理可能的 generic args 后缀
        if (cursor.Peek() == TokenKind.Lt) {
            head = WrapGeneric(head, ...);
        }
    }
    return head;
}
```

### SymbolTable.ResolveType MemberType 分支

```csharp
TypeExpr ResolveType(TypeExpr te) => te switch {
    NamedType nt   => /* 现有 */,
    MemberType mt  => ResolveMemberType(mt),
    GenericType gt => /* 现有，但内部递归 ResolveType 自然处理 MemberType */,
    ...
};

Z42Type ResolveMemberType(MemberType mt) {
    var leftType = ResolveType(mt.Left);
    if (leftType is Z42ClassType ct) {
        if (ct.NestedDelegates is { } nested 
            && nested.TryGetValue(mt.Right, out var di)
            && di.TypeParams.Count == 0) {
            return di.Signature;
        }
        // try generic key with arity hint? — out of scope for v1
        _diags.Error(DiagnosticCodes.UndefinedSymbol,
            $"type `{ct.Name}` has no nested type `{mt.Right}`",
            mt.Span);
        return Z42Type.Error;
    }
    _diags.Error(DiagnosticCodes.TypeMismatch,
        $"cannot access nested type on non-class type",
        mt.Span);
    return Z42Type.Error;
}
```

注：`Z42ClassType` 是否已有 `NestedDelegates` 字段需要复核；若无，从 SymbolCollector 把现有 qualified-key map 暴露上来。

## Testing Strategy

- **单元测试**：
  - Parser：`Btn.OnClick` / `Btn.OnClick<T>` / `A.B.C` 都解析为正确 MemberType / GenericType 链
  - TypeChecker：外部 dotted-path 字段 / 参数 / 返回类型 ✅；不存在的 nested 名 ❌ E0401；左侧非 class ❌
- **golden test**：`nested_delegate_dotted` —— 跨类引用 nested delegate，编译 + 运行 + 调用
- **VM 验证**：dotnet test + ./scripts/test-vm.sh
