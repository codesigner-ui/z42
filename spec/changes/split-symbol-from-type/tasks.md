# Tasks: Split Symbol Layer from Z42Type

> 状态：🟡 进行中 | 创建：2026-05-10
> 类型：lang（架构性，per workflow.md 完整流程）
> 关联：[docs/review.md](../../../docs/review.md) Part 2 §2.3 + Part 3 §3.1

## 进度概览

- [x] 阶段 1: Symbol 基础设施（NEW 文件 + Z42ClassType cyclic-equality fix）
- [ ] 阶段 2: SymbolCollector 构造 + TypeChecker 49 处迁移
- [ ] 阶段 3: Codegen 14 处 + ImportedSymbolLoader symbol 化
- [ ] 阶段 4: BoundCall.Symbol + BoundIndirectCall + 5 visitor 子类
- [ ] 阶段 5: TestAttributeValidator + IrGen TestIndex + BoundDumper
- [ ] 阶段 6: 文档同步 + 归档

每 Phase 独立 commit + 全绿验证（`dotnet test` + `./scripts/test-vm.sh`）。

---

## 阶段 1: Symbol 基础设施

### 1.1 NEW Symbol 接口与实现

- [x] 1.1.1 NEW [src/compiler/z42.Semantics/Symbols/IMemberSymbol.cs](../../../src/compiler/z42.Semantics/Symbols/IMemberSymbol.cs):
  - `IMemberSymbol` 基接口：`Name` / `Span` / `Visibility` / `ContainingType: Z42Type?`
- [x] 1.1.2 NEW [src/compiler/z42.Semantics/Symbols/IMethodSymbol.cs](../../../src/compiler/z42.Semantics/Symbols/IMethodSymbol.cs):
  - `IMethodSymbol : IMemberSymbol` 接口
  - 默认接口属性 `IsStatic`/`IsVirtual`/`IsAbstract`/`IsOverride`/`IsExtern` 从 `Modifiers.HasFlag` 派生
  - `MethodSymbol` sealed class 实现：手写 `Equals`/`GetHashCode` based on `(ContainingType?.Name 短名, Name, Signature)`
- [x] 1.1.3 NEW [src/compiler/z42.Semantics/Symbols/IFieldSymbol.cs](../../../src/compiler/z42.Semantics/Symbols/IFieldSymbol.cs):
  - `IFieldSymbol : IMemberSymbol` 接口
  - `FieldSymbol` sealed class 实现：手写 `Equals`/`GetHashCode` based on `(ContainingType?.Name 短名, Name, Type)`
- [x] 1.1.4 NEW [src/compiler/z42.Semantics/Symbols/README.md](../../../src/compiler/z42.Semantics/Symbols/README.md):
  - 目录职责 + 核心文件 + 入口点 + 不变量（per code-organization.md）

### 1.2 Z42ClassType 自定义 Equals + Rebuild helper（保留 sealed record）

> 实施时调整：原计划 sealed class + SetMembers 因 `Only records may inherit from records` C# 规则不可行（Z42Type 是 abstract record，11 个子类必须保留 record）。改为保留 record + 自定义 Equals 排除 member dicts，循环引用问题同样解决。详见 design.md Decision 7 修订。

- [x] 1.2.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/Z42Type.cs](../../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs):
  - `Z42ClassType` 保留 `sealed record`
  - 添加 `Equals(Z42ClassType?)` override：仅按 `(Name, IsStruct)` 比较，排除 member dicts
  - 添加 `GetHashCode()` override：`HashCode.Combine(Name, IsStruct)`
  - 添加 `Rebuild(...)` helper（`with`-expression 的命名等价物，方便 Phase 2 构造）
  - 字典值类型保持 `Z42FuncType`/`Z42Type`（阶段 2 才切换）
- [x] 1.2.2 检查 `Z42InterfaceType` 不需改 —— 它已经有自定义 Equals 跳过 Methods 字典
- [x] 1.2.3 检查 `with` expression 使用：3 处 (`SymbolCollector.Impls.cs:106` / `SymbolCollector.Classes.cs:341` / `:503`) 仍然有效（record 没变）
- [x] 1.2.4 `dotnet build src/compiler/z42.slnx` 通过
- [x] 1.2.5 `dotnet test` 全绿（1176/1176，行为不变）
- [ ] 1.2.6 commit: `feat(compiler): split-symbol-from-type Phase 1 — Symbol interfaces + Z42ClassType cyclic-equality fix`

## 阶段 2: SymbolCollector 构造 IMethodSymbol/IFieldSymbol + TypeChecker 49 处迁移

### 2.1 SymbolCollector 构造 Symbol（携带 Decl）

- [ ] 2.1.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs](../../../src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs):
  - 收集类成员时构造 `MethodSymbol(decl: methodDecl, ...)` / `FieldSymbol(decl: fieldDecl, ...)`
  - 两阶段构造：先 `var classType = new Z42ClassType(name, ...)`；后 `classType.SetMembers(fields, methods, ...)`
  - Modifiers 从 `decl.Modifiers` 拷贝
  - TestAttributes 从 `decl.TestAttributes` 引用（不复制 list）
- [ ] 2.1.2 MODIFY [src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs](../../../src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs):
  - 同上，处理顶层函数 / 接口收集时构造 Symbol
- [ ] 2.1.3 MODIFY [src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Impls.cs](../../../src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Impls.cs):
  - impl 块方法构造 `MethodSymbol`

### 2.2 Z42ClassType / Z42InterfaceType 字典值切换

- [ ] 2.2.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/Z42Type.cs](../../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs):
  - `Z42ClassType.Fields/Methods/StaticFields/StaticMethods` 字典值类型: `Z42Type/Z42FuncType` → `IFieldSymbol/IMethodSymbol`
  - `Z42InterfaceType.Methods` 同步切换：`MethodSignature` → `IMethodSymbol`
  - `Z42StaticMember` (line 345) 检查是否需要同步迁移

### 2.3 TypeChecker 49 处调用点迁移

- [ ] 2.3.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs):
  - 24 处 `cls.Methods[name]` / `cls.Fields[name]` 改用 `.Signature` / `.Type` 访问
  - overload resolution 路径检查
- [ ] 2.3.2 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs):
  - 12 处迁移
- [ ] 2.3.3 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs):
  - 8 处迁移
- [ ] 2.3.4 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs):
  - 3 处迁移
- [ ] 2.3.5 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs):
  - 2 处迁移
- [ ] 2.3.6 grep 验证：`grep -n "cls\.Methods\[" src/compiler/z42.Semantics/` 检查无遗漏（除 ContainsKey 检查类）
- [ ] 2.3.7 `dotnet test` 全绿（含 757 既有 TypeChecker 测试）
- [ ] 2.3.8 commit: `refactor(compiler): split-symbol-from-type S2 — SymbolCollector + TypeChecker 49 sites`

## 阶段 3: Codegen 14 处 + ImportedSymbolLoader symbol 化

### 3.1 Codegen 调用点迁移

- [ ] 3.1.1 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.Members.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.Members.cs):
  - 6 处 `cls.Methods.ContainsKey` / `cls.Fields.ContainsKey` 验证 dict 值类型已切换无需更多修改
- [ ] 3.1.2 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs):
  - 4 处 ContainsKey 检查
- [ ] 3.1.3 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs):
  - 1 处 `cls.Methods` 访问

### 3.2 ImportedSymbolLoader symbol 构造

- [ ] 3.2.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs](../../../src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs):
  - 三阶段加载内部：构造 `MethodSymbol(decl: null, modifiers: <from TSIG>, span: <from TSIG or Empty>, ...)`
  - 同步 `FieldSymbol(decl: null, ...)` 路径
- [ ] 3.2.2 检查 `ExportedClass` / TSIG wire 是否携带 `Modifiers` / `Span` 信息：
  - 如果不携带：先用合理默认（Public + Empty Span），加 TODO 注释；扩 wire format 留独立 follow-up spec
  - 如果携带：直接读
- [ ] 3.2.3 MODIFY [src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs](../../../src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs):
  - 合成 ClassDecl / FunctionDecl 时同步构造 symbols（类似 ImportedSymbolLoader 但 Decl 非空——因为合成的 AST 节点存在）

### 3.3 验证

- [ ] 3.3.1 `dotnet test` 全绿
- [ ] 3.3.2 `./scripts/test-vm.sh` 全绿（codegen 行为应零变化）
- [ ] 3.3.3 commit: `refactor(compiler): split-symbol-from-type S3 — Codegen + ImportedSymbolLoader symbol-aware`

## 阶段 4: BoundCall.Symbol + BoundIndirectCall + 5 visitor 子类

### 4.1 Bound 节点改造

- [ ] 4.1.1 MODIFY [src/compiler/z42.Semantics/Bound/BoundExpr.cs](../../../src/compiler/z42.Semantics/Bound/BoundExpr.cs):
  - `BoundCall` 加 `IMethodSymbol Symbol` 字段（非空）
  - `BoundMember` 加 `IMemberSymbol? Symbol` 字段（可空）
  - NEW `BoundIndirectCall(BoundExpr Callee, IReadOnlyList<BoundExpr> Args, Z42Type RetType, Span Span) : BoundExpr(RetType, Span)`

### 4.2 BoundExprVisitor 加 case + 5 个子类全员加 override

- [ ] 4.2.1 MODIFY [src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs](../../../src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs):
  - 基类 switch 加 `BoundIndirectCall ic => VisitIndirectCall(ic)`
  - 加 `protected abstract TResult VisitIndirectCall(BoundIndirectCall ic)`
  - `BoundExprWalker` 默认实现：`Visit(ic.Callee); foreach (a in ic.Args) Visit(a); return default;`
  - 5 个子类 build 失败 → 强制全员 override（这是 visitor 框架的核心收益兑现）
- [ ] 4.2.2 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs):
  - `IrEmitExprVisitor.VisitIndirectCall`：发射 indirect call IR（复用 `EmitBoundCall.Indirect` 路径或拆出新 helper）
- [ ] 4.2.3 MODIFY [src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs](../../../src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs):
  - `ReadsVisitor.VisitIndirectCall`：递归 Callee + Args（每个都 `Visit()`）
- [ ] 4.2.4 MODIFY [src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs](../../../src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs):
  - `EscapeExprScanner.VisitIndirectCall`：Callee 走 **非 callee 位置**（`calleePosition: false`）—— 间接调用的 callee 是个值，不是方法，应该 escape；Args 同样
- [ ] 4.2.5 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs):
  - `ClassRefScanner.VisitIndirectCall`：override 为 no-op（preserve 旧 BoundCall 间接路径不递归的语义）
- [ ] 4.2.6 MODIFY [src/compiler/z42.Pipeline/BoundDumper.cs](../../../src/compiler/z42.Pipeline/BoundDumper.cs):
  - `ExprDumper.VisitIndirectCall`：dump 输出 `BoundIndirectCall : <type> ...` + 缩进 Callee + Args
  - `ExprDumper.VisitCall`：增加 `decl=...` 标记（new BoundCall.Symbol）

### 4.3 TypeChecker 路径分流

- [ ] 4.3.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs):
  - 直接方法分派路径（Free/Static/Instance/Virtual）：构造 `BoundCall(..., Symbol: methodSymbol, ...)`
  - 间接调用路径（lambda 直接调 / 函数变量调 / 方法组转换后的函数值调）：构造 `BoundIndirectCall(Callee, Args, ...)`
  - 关键区分：被调用方的类型 + 上下文（call site 的 callee 表达式形态）

### 4.4 验证

- [ ] 4.4.1 `dotnet test` 全绿（含 closures / lambda 测试）
- [ ] 4.4.2 `./scripts/test-vm.sh` 全绿（IR 输出应等价 —— 间接调用走 CallIndirect，直接调用走 Call/VCall）
- [ ] 4.4.3 commit: `refactor(compiler): split-symbol-from-type S4 — BoundCall.Symbol + BoundIndirectCall + visitor`

## 阶段 5: TestAttributeValidator + IrGen TestIndex + BoundDumper

### 5.1 TestAttributeValidator 通过 Symbol 访问

- [ ] 5.1.1 MODIFY [src/compiler/z42.Semantics/TestAttributeValidator.cs](../../../src/compiler/z42.Semantics/TestAttributeValidator.cs):
  - 遍历 `symbolTable.Classes.Values` → `cls.Methods.Values` → `methodSymbol.TestAttributes`
  - 不再 walk `cu.Classes`
  - 行为完全等价（输出 diagnostic 字节相同）

### 5.2 IrGen TestIndex 收集

- [ ] 5.2.1 MODIFY [src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs](../../../src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs):
  - L183-219 R1 TestIndex 收集改用 `IMethodSymbol.TestAttributes`
  - 不再 walk `cu.Classes`
  - **关键验证**：生成的 TestIndex 字节级一致（VM 端不感知任何变化）

### 5.3 单元测试

- [ ] 5.3.1 NEW [src/compiler/z42.Tests/SymbolLayerTests.cs](../../../src/compiler/z42.Tests/SymbolLayerTests.cs):
  - `MethodSymbol_LocalClass_HasDeclBackPointer`
  - `MethodSymbol_Imported_HasNullDecl`
  - `MethodSymbol_Modifiers_FromDeclAtConstruction`
  - `MethodSymbol_ImportedHasModifiersFromTsig`（如果 TSIG 不携带 Modifiers，标 [Skip] + 关联 follow-up issue）
  - `MethodSymbol_Equals_BasedOnContainingNameAndSignature`
  - `Z42ClassType_SetMembers_IsSingleShot`（second call throws）
  - `Z42ClassType_Equals_BasedOnNameAndIsStruct`
- [ ] 5.3.2 NEW [src/compiler/z42.Tests/SymbolDeclSpanTests.cs](../../../src/compiler/z42.Tests/SymbolDeclSpanTests.cs):
  - `Method_DeclSpan_PointsToSourceLocation`
  - `Field_DeclSpan_PointsToSourceLocation`
  - `Symbol_AcrossPipeline_PreservesDecl`（端到端：parse → typecheck → 通过 Z42ClassType.Methods 访问 Symbol）
  - `BoundCall_DirectMethod_HasNonNullSymbol`
  - `BoundIndirectCall_LambdaInvocation_NotBoundCall`
  - `BoundIndirectCall_FunctionVariableInvocation_NotBoundCall`

### 5.4 BoundDumper Symbol 信息

- [ ] 5.4.1 BoundDumperTests.cs 扩展用例：`BoundCall_Dump_ShowsSymbolDeclLocation`
- [ ] 5.4.2 既有 BoundDumperTests 5 用例可能因为新增 `decl=...` 标记导致字符串断言失败 → 更新断言

### 5.5 验证

- [ ] 5.5.1 `dotnet test` 全绿（含新增 ~12 测试）
- [ ] 5.5.2 `./scripts/test-vm.sh` 全绿
- [ ] 5.5.3 commit: `refactor(compiler): split-symbol-from-type S5 — TestAttributeValidator + IrGen + dumper symbol-aware`

## 阶段 6: 文档同步 + 归档

- [ ] 6.1 MODIFY [docs/design/compiler-architecture.md](../../../docs/design/compiler-architecture.md):
  - 增"Symbol 层"段落作为正面设计
  - 记录不变量：(1) Z42ClassType 不再持有签名；(2) Symbols 持有 Decl back-pointer；(3) 构造两阶段 + frozen
  - 列举消费者（TypeChecker / Codegen / TestAttributeValidator / BoundDumper）
- [ ] 6.2 MODIFY [src/compiler/z42.Semantics/README.md](../../../src/compiler/z42.Semantics/README.md):
  - 核心文件表加 `Symbols/IMemberSymbol.cs` / `IMethodSymbol.cs` / `IFieldSymbol.cs`
- [ ] 6.3 MODIFY [docs/review.md](../../../docs/review.md):
  - Part 2 §2.3 + Part 3 §3.1 状态 📋 → 🟢 2026-05-10
  - 优先级清单清空（split-symbol-from-type 是最后一项）
  - 修订记录追加
- [ ] 6.4 tasks.md 状态改 🟢 已完成
- [ ] 6.5 移动 `spec/changes/split-symbol-from-type/` → `spec/archive/2026-05-10-split-symbol-from-type/`
- [ ] 6.6 commit: `docs+spec(compiler): split-symbol-from-type — archive + architecture doc sync`
- [ ] 6.7 push origin main

## 备注

### 实施依赖
- introduce-bound-visitor (visitor 框架) — ✅ 已落地
- impl-dump-ast (BoundDumper 框架) — ✅ 已落地
- 无 VM / Rust runtime 依赖

### 不解决的问题（follow-up spec 处理）
- IPropertySymbol / IParameterSymbol / INamespaceSymbol / IEventSymbol — `extend-symbol-layer` spec
- BoundIdent.SourceSymbol（方法组转换 back-pointer）— 独立 spec
- TSIG / ExportedClass 扩展携带 Modifiers/Span（如果 wire format 缺失）— `extend-tsig-metadata` spec
- M7 反射 R-series 各 phase API surface — 各自独立 spec

### 关键风险监控
- **Phase 1 验证点**：`dotnet test` 全绿 → 证明 Z42ClassType 改 sealed class 没破坏现有相等性 / with-expression 假设
- **Phase 2 验证点**：49 处迁移后全绿 → 证明字典值类型切换是机械替换、行为零变化
- **Phase 4 验证点**：`./scripts/test-vm.sh` IR 字节级抽查 → 证明 BoundIndirectCall 拆分不影响 codegen 输出（间接调用走 CallIndirect / 直接走 Call / VCall）
- **Phase 5 验证点**：TestIndex 字节级一致 → 证明 TestAttributeValidator + IrGen 切换 Symbol 路径行为完全等价
