# Proposal: Split Symbol Layer from Z42Type

## Why

[docs/review.md](../../../docs/review.md) Part 2 §2.3 + Part 3 §3.1 — z42 编译器的 `Z42ClassType` 把**类型身份**（Name / TypeParams / BaseClassName）和**声明身份**（Fields / Methods / StaticFields / StaticMethods 字典）混在一个 record 里。这造成两个互相纠缠的问题：

1. **Decl 身份在 pipeline 中丢失**（§3.1）：AST `FunctionDecl` / `FieldDecl` 在 SymbolCollector 之后被折叠进 `Z42ClassType.Methods` 字典（值是 `Z42FuncType` 签名），原始 Decl 节点不再可达。BoundCall 只有字符串名 `MethodName` + `ReceiverClass`，零 back-pointer 到源 decl。Codegen 想知道方法的 [Test] 属性必须重新走 `cu.Classes` AST。R1 `IrGen.Generate.cs:183-219` 已经在做这件事——架构在 R-series 反射前就开始破。

2. **缺 Symbol 层**（§2.3）：没有 `IMethodSymbol` / `IFieldSymbol` / `IPropertySymbol` 等 Roslyn 风格 first-class 符号对象。所有成员查询都通过 `Z42ClassType.Methods["foo"]` 字典 lookup，63 个调用点散落在 9 个文件。`ImportedSymbolLoader` 的 675 行 / 三阶段加载（skeleton → fill → constraints）部分原因就是"用类型对象表达符号"——类型实例和符号身份混在一起。

**M7 反射 R-series 触发的紧迫性**：当 R-series 反射 API 需要"运行时取成员定义位置 / 取成员的 attribute 列表"等能力时，必须能从 `BoundCall` / 运行时 metadata 反向追到原始 Decl。继续基于现有 `Z42ClassType` 设计 R-series → 后续回头改成本翻倍（[feedback_design_integrity](../../../.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/feedback_design_integrity.md) 典型场景：设计已偏移就立即停下重构，不硬塞）。

**不做会怎样**：M7 反射 R-series 落地前每个新 pass 都要双重维护"通过 Z42ClassType 拿签名 + 通过原始 AST 拿元数据"两套通道；Diagnostic 提升（`X declared at file:line` 类风格升级）做不出来；ImportedSymbolLoader 复杂度持续累积。

## What Changes

引入 z42 编译器的 **Symbol 层**：把"成员声明的身份 + 元数据"从 `Z42ClassType` 拆出，单独建模。Roslyn / Clang 验证过的架构。

### 核心新增

- `Z42.Semantics.Symbols` 命名空间（NEW）：
  - `IMemberSymbol`（基接口）：`Name` / `Span` / `Visibility` / `ContainingClass`
  - `IMethodSymbol : IMemberSymbol`：`Signature: Z42FuncType` / `IsStatic` / `IsVirtual` / `IsAbstract` / `IsOverride` / `Modifiers` / `TestAttributes` / `Decl: FunctionDecl?`
  - `IFieldSymbol : IMemberSymbol`：`Type: Z42Type` / `IsStatic` / `IsEvent` / `Decl: FieldDecl?`
  - 实现记录（sealed record）：`MethodSymbol` / `FieldSymbol` / `ImportedMethodSymbol` / `ImportedFieldSymbol`（imported 变体 `Decl == null`）

### 修改

- `Z42ClassType.Methods/Fields/StaticMethods/StaticFields` 字典值类型：`Z42FuncType`/`Z42Type` → `IMethodSymbol`/`IFieldSymbol`
- `BoundCall` 加一个 `IMethodSymbol? Symbol` 字段（back-pointer，方便 Codegen / 后续 reflection 跳过名字 re-lookup；当前 codegen 仍可只用名字 fallback）
- `BoundMember` 加一个 `IMemberSymbol? Symbol` 字段（field/property access 的 back-pointer）
- `ImportedSymbolLoader` 输出 `ImportedMethodSymbol(decl: null)` / `ImportedFieldSymbol(decl: null)`；本地 SymbolCollector 输出携带 Decl 的版本
- `BoundCallKind` 等枚举不变；只增加一个**可选** symbol 字段
- 63 个 `Z42ClassType.Methods[name].ParamTypes` 风格调用点迁移到 `.Methods[name].Signature.ParamTypes`（mechanical）
- TestAttributeValidator / R1 TestIndex 收集改为通过 `IMethodSymbol.TestAttributes` 而不是 re-walk `cu.Classes`

### 不修改（明确不做）

- **不引入** Roslyn 完整 ISymbol surface（IPropertySymbol / IParameterSymbol / INamespaceSymbol / IAssemblySymbol / IEventSymbol 等）—— 留 follow-up `extend-symbol-layer` spec
- **不改** Z42Type 的其他形状（PrimType / FuncType / ArrayType / OptionType / GenericParamType / EnumType / InstantiatedType 全部不动）
- **不改** SymbolTable 公共 API 表面（其内部 Classes dict value 类型变化属于实现细节）
- **不引入** AST visitor 框架（独立 spec）
- **不**为 R-series 反射 API 提供具体接口表面 —— 本 spec 只准备 Symbol 基础设施，反射 API 在 M7 R-series 各 phase 单独 spec
- **不改** zbc / zpkg 二进制格式 —— Symbol 是编译期/运行期内存模型，wire format 不动
- **不改** VM 端 —— Rust runtime 零变更（Symbol 是 C# 编译器内部抽象）

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---------|------|------|
| `src/compiler/z42.Semantics/Symbols/IMemberSymbol.cs` | NEW | `IMemberSymbol` 基接口 |
| `src/compiler/z42.Semantics/Symbols/IMethodSymbol.cs` | NEW | `IMethodSymbol` 接口 + `MethodSymbol` / `ImportedMethodSymbol` records |
| `src/compiler/z42.Semantics/Symbols/IFieldSymbol.cs` | NEW | `IFieldSymbol` 接口 + `FieldSymbol` / `ImportedFieldSymbol` records |
| `src/compiler/z42.Semantics/Symbols/README.md` | NEW | 目录说明（per code-organization.md） |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `Z42ClassType` 的四个字典值类型改 `IMethodSymbol`/`IFieldSymbol`；`Z42InterfaceType` 同步 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | MODIFY | 构造 `MethodSymbol`/`FieldSymbol` 时携带 `Decl` 反指针 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs` | MODIFY | 同上，类成员收集 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Impls.cs` | MODIFY | impl 块成员构造 symbol |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | MODIFY | 输出 `ImportedMethodSymbol(decl: null)` / `ImportedFieldSymbol(decl: null)` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` | MODIFY | 12 处成员查询改 `.Signature` 访问；overload resolution 路径 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | 2 处成员查询 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs` | MODIFY | 24 处（最多）成员查询迁移 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs` | MODIFY | 8 处成员查询 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs` | MODIFY | 3 处成员查询 |
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | `BoundCall` + `BoundMember` 加 `IMemberSymbol? Symbol` 可选字段 |
| `src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs` | (no change) | record 增字段不影响 visitor switch |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` | MODIFY | 1 处成员查询 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.Members.cs` | MODIFY | 6 处 `.Methods.ContainsKey` / `.Fields.ContainsKey` |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs` | MODIFY | 4 处成员查询 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs` | MODIFY | TestIndex 收集改用 `IMethodSymbol.TestAttributes` |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | 改用 symbol-based 遍历 |
| `src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs` | MODIFY | 合成 ClassDecl 时同步构造 symbols（少量影响） |
| `src/compiler/z42.Semantics/README.md` | MODIFY | 更新核心文件表（含 Symbols/ 目录） |
| `src/compiler/z42.Tests/SymbolLayerTests.cs` | NEW | Symbol 构造 / Decl 反指针 / Imported 区分单测 |
| `src/compiler/z42.Tests/SymbolDeclSpanTests.cs` | NEW | 验证 IMethodSymbol/IFieldSymbol 持有正确的 DeclSpan / FunctionDecl 反指针 |
| `docs/design/compiler-architecture.md` | MODIFY | 增 "Symbol 层" 段落作为正面设计；记录 IMethodSymbol/IFieldSymbol 不变量 |

**只读引用**（理解必须读，不修改；不计入并行冲突）：

- `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` — 公共 API 不变
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` 其他 record（不动）
- `src/compiler/z42.Syntax/Parser/Ast.cs` `FunctionDecl` / `FieldDecl` 定义（被引用为 back-pointer）
- `docs/review.md` Part 2 §2.3 / Part 3 §3.1（立项依据）
- `src/compiler/z42.IR/`、`src/compiler/z42.Project/`、`src/runtime/`（不动）

**估计文件数**：~22 个修改 + 5 个 NEW = 27 个具体文件。

## Out of Scope

- **Rust VM 端任何改动** —— Symbol 是 C# 编译器内部抽象，运行期 metadata 不变
- **zbc / zpkg / TSIG wire format 任何改动** —— wire format 仍是 Z42FuncType-shaped 签名信息
- **R-series 反射 API 接口设计** —— 本 spec 只准备基础设施，reflection API 留各 phase spec
- **IPropertySymbol / IParameterSymbol / INamespaceSymbol** —— 留独立 follow-up `extend-symbol-layer` spec
- **AST visitor 框架** —— 独立 spec `introduce-ast-visitor`
- **`introduce-bound-visitor` 已落地的 BoundExprVisitor 接口** —— record 增字段不影响 visitor 抽象
- **性能优化** —— Symbol 层引入的开销（每 method 多一个 wrapper 对象 + dict value 间接）应可忽略；profile 留 follow-up
- **重命名 `Z42ClassType` 为 `Z42NamedTypeSymbol` 等 Roslyn 风格命名** —— pre-1.0 不引入大批重命名
- **删除 SymbolTable** —— 它仍是 Pass 0 → Pass 1 的数据边界，不动

## Open Questions

- [ ] **`Z42InterfaceType.Methods` 是否一并迁移**：当前 `Z42InterfaceType` 持有 `IReadOnlyDictionary<string, MethodSignature>` 而不是 `Z42FuncType`。是否一起改成 `IMethodSymbol`？倾向**一起改**（保持类/接口对称），但若 MethodSignature 引入额外信息（IsStatic/IsVirtual 修饰符等）则需要 design.md 设计。Decision 留给 design.md
- [ ] **IMethodSymbol 中的 Modifiers 字段是否冗余**：`MethodSymbol` 已经有 `IsStatic` / `IsVirtual` 等 bool；与 `Decl?.Modifiers` 重复。倾向 **bool 字段权威，Decl 是源**（imported symbols 没 Decl）。Decision 留 design.md
- [ ] **BoundCall.Symbol 是否始终非空**：本地 call 可以查到 symbol，但跨 zpkg call 是否也能拿到？倾向 **始终非空**（imported symbols 也是 IMethodSymbol，只是 `Decl == null`）。如果某些路径暂时拿不到 symbol，先标 nullable，逐步收紧。Decision 留 design.md
- [ ] **`IMemberSymbol.ContainingClass` 类型**：是 `string` 还是 `Z42ClassType`？倾向 **`string` 当前**（避免循环引用 + Z42ClassType 包含 IMethodSymbol，IMethodSymbol 又指回去）。Decision 留 design.md
- [ ] **是否给 `Z42FuncType` 加 `BackingSymbol: IMethodSymbol?` back-pointer**：会导致 Z42FuncType 不再是纯结构性类型（structural equality 受影响）。倾向 **不加**（让 Symbol 持有 FuncType，反之不持），保留 Z42FuncType 的纯结构性。Decision 留 design.md
