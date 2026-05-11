# Tasks: Extend Named Args to Local Nested + Cross-CU

> 状态：🟢 已完成 | 创建：2026-05-12 | 完成：2026-05-12
> 类型：refactor/fix（最小模式 — 扩展已归档 lang spec 的延期项）
> 前置：✅ [`add-named-arguments`](../../archive/2026-05-12-add-named-arguments/)

**变更说明**：消除 add-named-arguments 归档时保留的两处 Z1002 fallback shim：
1. 嵌套局部函数（`LocalFunctionStmt`）— TypeEnv 未存 decl
2. Imported / 跨 CU callable（class methods + interface methods + free funcs）— ImportedSymbolLoader 丢弃了 TSIG 已有的 param names

**原因**：归档 spec 的 shim 假设需要扩 Z42FuncType.ParamNames 或 zpkg 格式。重新审视发现 TSIG/SIGS section 已编码 `ExportedMethodDef.Params[i].Name` + `ExportedFunctionDef.Params[i].Name`，**无 wire format 变更**，只需在 import 端恢复 param names 即可。

**文档影响**：
- 无 wire format 变更 → 无 `docs/design/runtime/ir.md` 更新
- 无新语法 → 无 `docs/design/language/language-overview.md` 更新
- 归档 spec 的 deferred 注释（"局部嵌套函数"、"imported 跨 CU 函数"）通过本 spec 完结

## 进度概览

- [x] 阶段 1: TypeEnv 接入嵌套局部函数
- [x] 阶段 2: ImportedSymbolLoader 合成 FunctionDecl 携带 params
- [x] 阶段 3: BindArgsReordered 支持 cross-CU optional default → BoundDefault(type)
- [x] 阶段 4: 测试 + 验证

## 阶段 1: TypeEnv 接入嵌套局部函数

- [x] 1.1 [TypeEnv.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs) — 加 `_localFuncDecls: Dictionary<string, FunctionDecl>`；`DefineLocalFunc` 增加 `FunctionDecl decl` 参数；新增 `LookupLocalFuncDecl(string name) → FunctionDecl?`（沿链查找）
- [x] 1.2 [TypeChecker.Stmts.cs L39](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs) — `DefineLocalFunc(lf.Decl.Name, sig)` → `DefineLocalFunc(lf.Decl.Name, sig, lf.Decl)`
- [x] 1.3 [TypeChecker.Calls.cs L408 free-func 分支](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs) — Decl 查找优先级：local nested → SymbolTable.FuncDecls（保留现有 imported fallback）

## 阶段 2: ImportedSymbolLoader 合成 FunctionDecl

- [x] 2.1 [ImportedSymbolLoader.Phase2.cs L43-56](../../../../src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.Phase2.cs) class methods 路径 — 从 `m.Params[i].Name` + `m.Params[i].TypeName` 合成 `Param` 列表；wrap 为 `FunctionDecl`；传给 `MethodSymbol(..., decl: syntheticDecl)`
- [x] 2.2 同上 — Interface methods 路径（同 file L102）同样合成 decl
- [x] 2.3 [ImportedSymbolLoader 其他文件](../../../../src/compiler/z42.Semantics/TypeCheck/) — Imported free funcs 路径：把 ExportedFunctionDef 合成 FunctionDecl 后写入 SymbolCollector._funcDecls（与 local funcs 共用）
- [x] 2.4 合成 Param 的字段约定：`Type = new NamedType(typeName, span)`（占位，BindArgsReordered 不读 Type）；`Default = null`（TSIG 不携带 default expr —— 跨 CU 已知限制，与 fix-default-param-cross-cu D-9 一致）；`Modifier = ParamModifier.None`（跨 CU 不传 ref/out/in；现有 modifier-aware overload 在 TSIG 已用 name suffix `$N$<modSig>` 区分）；`Span = default`

## 阶段 3: BindArgsReordered 支持跨 CU optional default

- [x] 3.1 [TypeChecker.Calls.Modifiers.cs BindArgsReordered](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Modifiers.cs) — 增加 optional `Z42FuncType? sig` 参数；当 slot missing & `calleeParams[i].Default == null` & `sig != null` & `i >= sig.MinArgCount` → 用 `new BoundDefault(sig.Params[i], callSpan)` 替代 Z1005；其他情况保留 Z1005
- [x] 3.2 10 个 call sites 升级 — 传 `sig:` 让 cross-CU 跳过中间 default 的具名实参 binds clean

## 阶段 4: 测试 + 验证

- [x] 4.1 [NamedArgumentsTests.cs](../../../../src/compiler/z42.Tests/NamedArgumentsTests.cs) — 加 5 个 case：
  - 嵌套局部函数 reorder + 嵌套局部函数 Z1002
  - imported class method reorder（构造 ExportedClassDef + ExportedMethodDef）
  - imported class method Z1002
  - imported free function reorder（构造 ExportedFuncDef）
- [x] 4.2 全绿验证：1233 C# + 320 VM golden（2026-05-12）
- [x] 4.3 归档：mv → `docs/spec/archive/2026-05-12-extend-named-args-shim/`
