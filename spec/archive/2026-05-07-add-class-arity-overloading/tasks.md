# Tasks: add-class-arity-overloading

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：lang/ir（完整流程；VM 透明）
>
> **实施期 design pivot**（2026-05-07）：原 design.md 提出"永远 mangle generic class"导致需要 stdlib 全部 regen + IR 消费者全面改写（接近 1000 行）；实施中改为 **shadow-only mangling** —— 只在与同源非泛型类冲突时 generic 一方才 mangle，非冲突 generic 类（List<T> / Dictionary<K,V> / MulticastAction<T> 等）保持 bare key 完全不变。结果：实施工作量降到 ~250 行 + 0 stdlib 影响 + 0 VM 改动；功能等价（D-8b-1 / D-8b-3 Phase 2 阻塞同样解除）。

## 进度概览

- [x] 阶段 1: Z42ClassType.IrName + 单测
- [x] 阶段 2: SymbolCollector / SymbolTable / TypeEnv 注册 + 解析
- [x] 阶段 3: ImportedSymbolLoader 跨 zpkg key 对齐
- [x] 阶段 4: IrGen / FunctionEmitter emit IrName
- [x] 阶段 5: stdlib regen + 现有用例回归
- [x] 阶段 6: 新 golden + C# 单测
- [x] 阶段 7: 文档同步 + 验证 + 归档

---

## 阶段 1: 数据结构

- [x] 1.1 [Z42Type.cs](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) `Z42ClassType` 增 `IrName` 派生 property（generic → `Name${TypeParams.Count}`，否则 `Name`）

## 阶段 2: 编译器注册 / 解析

- [x] 2.1 [SymbolCollector.Classes.cs](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs) pre-pass / collect / inheritance merge / duplicate 路径全部用 IrName 作 key
- [x] 2.2 [SymbolCollector.cs](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs) ResolveType GenericType 分支用 `$"{gt.Name}${gt.TypeArgs.Count}"` lookup（NamedType 保持裸 lookup，对应 arity-0 槽位）
- [x] 2.3 [SymbolTable.cs](src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs) 镜像 ResolveType + 内部 lookup 同步
- [x] 2.4 [TypeEnv.cs](src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs) `CurrentClass` 字段语义改为 IrName（注释 + 调用方）
- [x] 2.5 [TypeChecker.Stmts.cs / Exprs.cs / Exprs.Operators.cs / Calls.cs / GenericResolve.cs](src/compiler/z42.Semantics/TypeCheck) 每条 `_symbols.Classes.TryGetValue(...)` 审查；env.CurrentClass 路径改 IrName

## 阶段 3: 跨 zpkg

- [x] 3.1 [ImportedSymbolLoader.cs](src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs) Phase 1 / Phase 2 / MergeImpls 中的 classes / classNs / classConstraints / classInterfaces / classPackages 等并行 dict 全部按 IrName key
- [x] 3.2 ClassNamespaces / ClassPackages 验证（key 与 _classes 同步）

## 阶段 4: IR 与 codegen

- [x] 4.1 [IrGen.cs](src/compiler/z42.Semantics/Codegen/IrGen.cs) class IR 名 emit 用 IrName（IrFunction 名 + IrClass 名 + 关联 class 引用）
- [x] 4.2 [FunctionEmitter.cs / FunctionEmitterCalls.cs / FunctionEmitterExprs.cs / FunctionEmitterStmts.cs](src/compiler/z42.Semantics/Codegen) ObjNew / FieldGet / VCall / IsInstance / AsCast / CallInstr 接收 className 全部经 IrName
- [x] 4.3 [TestAttributeValidator.cs](src/compiler/z42.Semantics/TestAttributeValidator.cs) attr.TypeArg lookup 兼容 NamedType / GenericType 两种格式

## 阶段 5: 回归 + stdlib regen

- [x] 5.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 5.2 `cargo build` 无错（VM 透明，应该 build pass）
- [x] 5.3 `./scripts/build-stdlib.sh` 重生 stdlib zpkg（IR 名 mangled 化）
- [x] 5.4 `./scripts/regen-golden-tests.sh` 重生 golden .zbc
- [x] 5.5 `dotnet test` 全过（含现有 generic class 用例）
- [x] 5.6 `./scripts/test-vm.sh interp/jit` 全过
- [x] 5.7 `./scripts/test-cross-zpkg.sh` 全过
- [x] 5.8 `cargo test` 全过

## 阶段 6: 新 golden + C# 单测

- [x] 6.1 `src/tests/classes/arity_overloading/` golden — `class Foo` + `class Foo<R>` 同 CU 共存 + new + 字段 / 方法独立工作
- [x] 6.2 `src/tests/classes/arity_method_dispatch/` golden — `class Pair<A, B>` 实例 method dispatch
- [x] 6.3 [src/compiler/z42.Tests/ClassArityOverloadingTests.cs](src/compiler/z42.Tests/ClassArityOverloadingTests.cs) C# 单测：
  - IrName 派生（non-generic / arity 1 / arity 2）
  - SymbolCollector：`class Foo` + `class Foo<R>` 同 CU → 注册 2 项
  - 同 arity 重复仍报 E0408
  - ResolveType NamedType / GenericType 各自路由

## 阶段 7: 文档同步 + 归档

- [x] 7.1 [docs/design/generics.md](docs/design/generics.md) 加 "class arity overloading + IrName" 段
- [x] 7.2 [docs/deferred.md](docs/deferred.md) D-8b-0 移到"已落地"；D-8b-1 / D-8b-3 Phase 2 标记前置已解除（仍未做，但不再受 D-8b-0 阻塞）
- [x] 7.3 commit + push（`feat(lang+ir): class arity overloading via IrName`）
- [x] 7.4 归档 spec → `spec/archive/2026-05-07-add-class-arity-overloading/`

---

## 备注

### 关键风险与缓解

| 风险 | 缓解 |
|------|------|
| 现有大量 `_classes[name]` lookup 调用点（41+）改不全导致回归 | 阶段 5.5 dotnet test + 阶段 5.6 test-vm 双层验证；C# 强类型系统会显式报错（key 没找到） |
| Stdlib zpkg 不重新生成导致 IR 名混版本 | 阶段 5.3 `build-stdlib.sh` 强制重生 |
| 用户面 typeof / 诊断意外 mangled 字符串泄漏 | spec scenario 显式覆盖；`Name` 与 `IrName` 两层显式分离 |
| 跨 zpkg 旧 zbc 不兼容 | pre-1.0 不留兼容路径，全 regen |

### 验证场景与 spec.md 映射

| spec scenario | 验证位置 |
|--------------|---------|
| Both declared in same CU | golden `arity_overloading/` |
| User code disambiguates by arity | golden `arity_overloading/`（new Foo / new Foo<int>）|
| NamedType resolves to non-generic only | C# 单测 ResolveType_NamedType_PicksNonGeneric |
| GenericType resolves to matching arity | C# 单测 ResolveType_GenericType_MatchesByArity |
| Non-generic IR name is bare | C# 单测 IrName_Derivation |
| Generic IR name has $N suffix | C# 单测 IrName_Derivation + golden 验证 zbc |
| Multi-arity generic | C# 单测（Pair<A,B> → "Pair$2"）|
| stdlib regen with IrName | 阶段 5.3-5.6 全套验证 |
| Cross-zpkg generic class import | 现有 stdlib 用例（List<int> / Dictionary<K,V>）回归 |
| Diagnostics preserve user-facing name | 现有 error goldens 回归（错误消息不应有 `$N` 泄漏） |
| typeof returns user-facing name | 现有 33_typeof golden 回归 |
