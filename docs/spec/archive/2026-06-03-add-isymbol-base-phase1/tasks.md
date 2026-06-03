# Tasks: F2.2 Phase 1 — ISymbol base + GetSymbol(Expr) query

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor + 加法 API
> 来源：[`docs/review.md`](../../../review.md) Part 6 F2.2

## 变更说明

引入 `ISymbol` 基接口 + `SymbolKind` enum，作为现有 `IMemberSymbol` /
`IMethodSymbol` / `IFieldSymbol` 的统一父类型。沿用 z42 现有 Symbol 层
（split-symbol-from-type spec 已落地）的 sealed class 模式 + 不可变契约。

主要新能力：`SemanticModel.GetSymbol(Expr) -> ISymbol?` —— 利用 F2.3
Phase 1 落地的 `ExpressionBindings` dict（Expr → BoundExpr）+
`BoundCall.Symbol: IMethodSymbol?` 等已有的 symbol 反指针抽取。补齐了
F2.3 Phase 1 当时被阻塞的 3 个方法之一（`GetSymbol`）。

## 原因

review.md F2.2 完整版（10-14 天）包含 `INamedTypeSymbol` / `IParameterSymbol` /
`ILocalSymbol` / `ITypeParameterSymbol` / `OriginalDefinition` / `Construct` 等。
Phase 1 收紧到**"基接口 + 让现有 method/field 符号统一进 ISymbol 层 + 一个
真实 consumer（SemanticModel.GetSymbol）"** —— 一个 session 可完整闭环 +
解锁 F2.3 GetSymbol 方法。

Phase 2 (独立 spec)：`INamedTypeSymbol` + Z42ClassType/InterfaceType impl
Phase 3 (独立 spec)：`ILocalSymbol` / `IParameterSymbol` + GetDeclaredSymbol
Phase 4 (独立 spec)：`OriginalDefinition` / `Construct` generic 路径

## 文档影响

- `docs/review.md` F2.2 / Part 6 P1 状态 (🟡 Phase 1 done)
- `src/compiler/z42.Semantics/Symbols/README.md` 加 ISymbol 节

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Semantics/Symbols/ISymbol.cs` | NEW | 基接口（Name / Kind / DeclarationSpan / ContainingSymbol?） |
| `src/compiler/z42.Semantics/Symbols/SymbolKind.cs` | NEW | enum: Method / Field / Class / Interface / Local / Parameter / TypeParameter / Namespace |
| `src/compiler/z42.Semantics/Symbols/IMemberSymbol.cs` | MODIFY | extends ISymbol；加默认 `DeclarationSpan => Span` 映射 |
| `src/compiler/z42.Semantics/Symbols/IMethodSymbol.cs` | MODIFY | `MethodSymbol` add `Kind => SymbolKind.Method` |
| `src/compiler/z42.Semantics/Symbols/IFieldSymbol.cs` | MODIFY | `FieldSymbol` add `Kind => SymbolKind.Field` |
| `src/compiler/z42.Semantics/Symbols/README.md` | MODIFY | 加 ISymbol 节 |
| `src/compiler/z42.Semantics/TypeCheck/SemanticModel.cs` | MODIFY | 加 `public ISymbol? GetSymbol(Expr astNode)` —— 内部基于 GetBoundExpression + BoundCall.Symbol 抽取 |
| `src/compiler/z42.Tests/SemanticModelQueryTests.cs` | MODIFY | 加 2-3 tests for `GetSymbol` |
| `docs/review.md` | MODIFY | F2.2 标 🟡 Phase 1 done |

只读引用：
- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` — `BoundCall.Symbol` 字段
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` — `Z42ClassType` 等

## 设计要点

### ISymbol surface (Phase 1 收紧)

```csharp
public interface ISymbol {
    string Name { get; }
    SymbolKind Kind { get; }
    Span DeclarationSpan { get; }
    Visibility Visibility { get; }
    // Phase 2 add: ISymbol? ContainingSymbol { get; }
    // Phase 2 add: bool Equals(ISymbol? other);
}
```

不引入 `ContainingSymbol` / `ContainingNamespace` —— 当前 IMemberSymbol 有
`ContainingType: Z42Type?` 但 Z42Type 不是 ISymbol（Phase 2 需要 ITypeSymbol
来桥接）。Phase 1 不引该字段避免 chicken-and-egg。

### `GetSymbol(Expr)` 抽取策略

```csharp
public ISymbol? GetSymbol(Expr astNode)
{
    var bound = GetBoundExpression(astNode);
    return bound switch
    {
        BoundCall { Symbol: { } sym } => sym,   // resolved direct method dispatch
        // Phase 2: BoundIdent, BoundMember, BoundIndex 等
        _ => null,
    };
}
```

Phase 1 仅支持 `BoundCall.Symbol` 抽取（最常用 case：调用 site）。
`BoundIdent` 的 ILocalSymbol 等待 Phase 3 引入。

### 不破坏现有 Symbol 层

`MethodSymbol` / `FieldSymbol` 已是 sealed class with manual Equals。
新增 `Kind` 只读 getter 是纯加法。`IMemberSymbol` 已有 `Name` / `Span` /
`Visibility` —— ISymbol 接口的字段已存在，inheritance 是无成本桥接。

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `Symbols/ISymbol.cs` + `Symbols/SymbolKind.cs`
- [x] 1.2 MODIFY `IMemberSymbol.cs` extends ISymbol + `DeclarationSpan => Span` default impl
- [x] 1.3 MODIFY `MethodSymbol` / `FieldSymbol` add `Kind` getter
- [x] 1.4 MODIFY `Symbols/README.md` 加 ISymbol 节
- [x] 1.5 MODIFY `SemanticModel.cs` 加 `GetSymbol(Expr) -> ISymbol?`（基于 BoundCall.Symbol）
- [x] 1.6 MODIFY `SemanticModelQueryTests.cs` 加 3 tests（direct call / literal null / unbound null）
- [x] 1.7 VERIFY `dotnet test` 1467/1467 全过
- [x] 1.8 MODIFY `review.md` F2.2 标 🟡 Phase 1 done + F2.3 改 3/5 done
- [x] 1.9 归档 + commit + push
