# Tasks: 引入 ref / out / in 参数修饰符（编译期验证）

> 状态：🟢 已完成 | 创建：2026-05-04 | 重命名 / 拆分 / 完成：2026-05-05

**本 spec 仅覆盖编译期验证。** 运行时实施（IR Codegen + VM Value::Ref + 端到端 golden）拆到 follow-up spec `impl-ref-out-in-runtime`。

## 进度概览
- [x] 阶段 1: Lexer (3 项)
- [x] 阶段 2: Parser/AST (7 项)
- [x] 阶段 3: TypeChecker (13 项含 Scope 扩展 + in 写保护)
- [x] 阶段 4: 文档同步 (5 项 — 仅编译期可写部分)
- [x] 阶段 5: 验证 (4 项)
- [ ] 阶段 6: 归档 (3 项)

## 阶段 1: Lexer
- [x] 1.1 `TokenKind.cs` 添加 `Ref` / `Out`
- [x] 1.2 `TokenDefs.cs` 注册 `"ref"` / `"out"` 关键字
- [x] 1.3 `LexerTests.cs` 添加 3 个 token 测试（Theory：`ref` / `out` / `in`；3/3 通过）

## 阶段 2: Parser/AST
- [x] 2.1 `Ast.cs` 添加 `ParamModifier` 枚举（None/Ref/Out/In）
- [x] 2.2 `Ast.cs` `Param` record 添加 `Modifier` 字段（默认 None，向后兼容）
- [x] 2.3 `Ast.cs` 添加 `ArgModifier` + `ModifiedArg` 节点
- [x] 2.4 `Ast.cs` 添加 `OutVarDecl` 节点
- [x] 2.5 `TopLevelParser.Helpers.cs` `ParseParamList` 支持前缀修饰符
- [x] 2.6 `ExprParser.cs` / `ExprParser.Atoms.cs` callsite 修饰符 + `out var x` 内联声明
- [x] 2.7 `ParserTests.cs` 添加 8 个解析测试（142/142 通过）

## 阶段 3: TypeChecker
- [x] 3.1 `TypeChecker.Calls.cs` 修饰符一致性检查（CheckArgModifiers + 12 处调用点）
- [x] 3.2 `TypeChecker.Calls.cs` lvalue 校验（`IsLvalueForRef`）
- [x] 3.3 `TypeChecker.Calls.cs` 严格类型匹配（CheckArgTypes 检测 BoundModifiedArg → exact equality）
- [x] 3.4 `TypeChecker.Calls.cs` overload resolution 增加 modifier 维度（ModifierMangling + LookupMethodOverload + free function 重载）
- [x] 3.5 `FlowAnalyzer.cs` DA 扩展：caller 端 post-call 视为已赋（BoundCall args 中 BoundModifiedArg with Out → mark assigned）
- [x] 3.6 `FlowAnalyzer.cs` DA 扩展：callee 端 normal-return 路径必赋（CheckDefiniteAssignment 接 functionParams；throw 路径除外）
- [x] 3.7 `TypeChecker.Exprs.cs` lambda 捕获禁止 + `TypeEnv.LookupParamModifier` 助手
- [x] 3.8 async / iterator 占位拒绝 — z42 当前未实现 async/iter，约束在引入时再加（natural restriction）
- [x] 3.9 generic `T` 拒绝 ref/out/in 形态 — modifier 不是类型，generic 实例化系统天然拒绝
- [x] 3.10 错误码段位 — 当前用 `DiagnosticCodes.TypeMismatch` 占位；阶段 4 文档说明（follow-up spec 落地运行时时统一分配 E04xx 段位）
- [x] 3.11 `ParameterModifierTypeCheckTests.cs` 新建 20 个语义测试（全绿）
- [x] 3.12 modifier-based overload key — 已落地：class methods + free functions 双侧支持
- [x] 3.13 `in` 参数写保护 — `BindAssign` 检测目标 ident 为 in param 时报错

## 阶段 4: 文档同步
- [x] 4.1 `language-overview.md` §5 重写函数小节（移除旧 `out int` 示例 + 完整三修饰符 + tuple 多返回对照 + 注明"运行时实施在 follow-up spec"）
- [x] 4.2 创建 `docs/design/parameter-modifiers.md` —— 完整编译期规范 + Deferred / Future Work（D1-D6）+ Runtime Implementation 段
- [x] 4.3 `interop.md` ABI 表新增 `ref T ↔ *mut T` / `out T ↔ *mut T` / `in T ↔ *const T` 映射
- [x] 4.4 `compiler-architecture.md` 加 "Parameter Modifiers" 段（数据流图 + 关键文件 + 用户视角规范引用）
- [x] 4.5 `roadmap.md` Pipeline 进度表加 ref/out/in 行（Parser/TypeCheck ✅，IrGen/VM ⏸）

## 阶段 5: 验证
- [x] 5.1 `dotnet build src/compiler/z42.slnx` —— 无编译错误
- [x] 5.2 `cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [x] 5.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 1032/1032 通过（含 31 新增：3 lexer + 8 parser + 20 typecheck）
- [x] 5.4 `./scripts/test-vm.sh` —— 256/256 通过（无回归）

## 阶段 6: 归档
- [ ] 6.1 tasks.md 状态改为 🟢 已完成 + 完成日期 2026-05-05
- [ ] 6.2 移动 `spec/changes/define-ref-out-in-parameters-typecheck/` → `spec/archive/2026-05-05-define-ref-out-in-parameters-typecheck/`
- [ ] 6.3 git commit + push（含 .claude/ 和 spec/，单一 commit）

## 备注
- **Spec 拆分（2026-05-05）**：原 spec `define-ref-out-in-parameters` 含 Phase 1-8（Lexer 到 VM 端到端）总 ~52 项任务。Phase 4-5（IR Codegen + VM）实施时发现真实工作量 ~25-30 turns 远超原估，且与 Phase 1-3 在不同抽象层（lang vs runtime），独立成 spec 更清晰。
- **拆分边界**：本 spec 限于编译期 + 文档；新 spec `impl-ref-out-in-runtime` 复用本 spec 的 design.md Decision 1/2/8/9 直接实施。
- **错误码段位**：当前 TypeChecker 用 `DiagnosticCodes.TypeMismatch` 占位。follow-up spec 落地时统一分配 E04xx 段位（保持占位至运行时实施同期归并是合理的，避免 codes 重复迁移）。
- **设计期延后特性**：D1 ref local / D2 ref return / D3 ref field / D4 ref struct / D5 scoped / D6 ref readonly —— 写入 `parameter-modifiers.md` 的 "Deferred / Future Work" 段，**不**进 `docs/deferred.md`（设计期延后规则）
- **pre-existing 规范不一致**（error-codes.md 段头 Z 前缀 vs 实际代码 E 前缀）—— 不在本 spec scope，待独立 fix spec 处理
- **JIT / AOT** 当前 L2 焦点是 interp，本 spec 不涉及 JIT/AOT 后端
- **运行时不一致警告**：用户在本 spec 落地后写 `Increment(ref c)` 编译期通过、运行时 callee 修改不传回 caller。`docs/design/parameter-modifiers.md` 的 "Runtime Implementation" 段需明确说明此过渡状态，并指引使用 tuple 多返回作为临时替代。
