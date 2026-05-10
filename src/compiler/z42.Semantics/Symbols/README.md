# z42.Semantics.Symbols

## 职责
Symbol 层 —— 把"成员声明的身份与元数据"从 `Z42Type` 拆出独立建模。
`IMethodSymbol` / `IFieldSymbol` 携带 `Decl` 反指针（本地非空，imported 为 null）+
`Span` + `Modifiers` + `TestAttributes`，是 `Z42ClassType.Methods` / `Z42InterfaceType.Methods`
的字典值类型。

## 核心文件
| 文件 | 职责 |
|------|------|
| `IMemberSymbol.cs` | 基接口 — `Name` / `Span` / `Visibility` / `ContainingType: Z42Type?` |
| `IMethodSymbol.cs` | 方法接口 + `MethodSymbol` sealed class 实现；含 `Signature` / `Modifiers` / `Decl?` / `TestAttributes?` 字段 |
| `IFieldSymbol.cs` | 字段接口 + `FieldSymbol` sealed class 实现；含 `Type` / `IsStatic` / `IsEvent` / `Decl?` 字段 |

## 入口点
- `Z42.Semantics.Symbols.MethodSymbol` — 构造方法符号；用于 `Z42ClassType.Methods` 字典值
- `Z42.Semantics.Symbols.FieldSymbol` — 同上，字段
- 接口 `IMethodSymbol` / `IFieldSymbol` 是消费方契约

## 不变量（不得破坏）
1. `MethodSymbol` / `FieldSymbol` 是 **sealed class**（不是 record），手写 `Equals`/`GetHashCode`
   based on `(ContainingType.Name 短名, Name, Signature/Type)` —— 避免 Z42ClassType 持有 IMethodSymbol
   持有 Z42ClassType 的循环引用导致默认 record Equals 无限递归
2. `Decl` 是 back-pointer，不参与相等性；本地非空，imported 为 null
3. `Modifiers` / `Span` / `TestAttributes` 是 single source of truth；本地构造时从
   `decl.Modifiers` / `decl.Span` / `decl.TestAttributes` 拷贝；imported 直接传入
4. `ContainingType: Z42Type?` —— class 成员是 `Z42ClassType`，interface 成员是 `Z42InterfaceType`，
   顶层自由函数 / 字段为 null
5. AST 不可变 + 构造时拷贝 → Symbol 内字段永不漂移

## 依赖关系
→ z42.Core（Span）
→ z42.Syntax（FunctionDecl / FieldDecl / TestAttribute / Visibility）
→ z42.Semantics.TypeCheck（Z42Type / Z42FuncType / Z42ClassType / Z42InterfaceType）
