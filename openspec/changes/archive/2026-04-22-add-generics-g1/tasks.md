# Tasks: L3-G1 泛型基础（泛型函数 + 泛型类，无约束）

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: AST + Parser ✅ (commit f7f5571)
- [x] 阶段 2: TypeChecker ✅ (commit e44e92f + 本次收尾)
- [x] 阶段 3: IrGen + 二进制格式 ✅ (commit 1d5a87b)
- [x] 阶段 4: VM ✅ (commit 1d5a87b + 281bce2)
- [x] 阶段 5: 测试与验证 ✅

## 阶段 1: AST + Parser ✅

- [x] 1.1 `Ast.cs`: FunctionDecl 新增 `List<string>? TypeParams = null`
- [x] 1.2 `Ast.cs`: ClassDecl 新增 `List<string>? TypeParams = null`
- [x] 1.3 `Ast.cs`: InterfaceDecl 新增 `List<string>? TypeParams = null`
- [x] 1.4 `Ast.cs`: 新增 `GenericType(string Name, List<TypeExpr> TypeArgs, Span)` TypeExpr
- [x] 1.5 `TopLevelParser.Helpers.cs`: 新增 `ParseTypeParams` + 保留 `SkipGenericParams`
- [x] 1.6 `TopLevelParser.cs`: ParseClassDecl/ParseInterfaceDecl 使用 ParseTypeParams
- [x] 1.7 `TopLevelParser.cs`: ParseFunctionDecl 解析泛型函数类型参数
- [x] 1.8 `TypeParser.cs`: 解析 `Name<TypeArg1, TypeArg2>` → `GenericType`
- [x] 1.9 `dotnet build` + `dotnet test` 458 passed

## 阶段 2: TypeChecker ✅

- [x] 2.1 新增 `Z42GenericParamType` 类型 + IsAssignableTo/IsReferenceType 兼容
- [x] 2.2 SymbolTable.PushTypeParams/PopTypeParams — 泛型参数作用域
- [x] 2.3 SymbolCollector: 函数签名收集时识别类型参数 T → Z42GenericParamType
- [x] 2.4 TypeChecker.BindFunction/BindClassMethods: push/pop type params
- [x] 2.5 GenericType TypeExpr 解析（SymbolTable + SymbolCollector）
- [x] 2.6 Golden test `68_generic_function` — 泛型函数类型推断调用
- [x] 2.7 泛型类实例化：`new Box<T>(42)` + 显式类型参数校验 `new Box<int>(42)`
- [x] 2.8 Z42ClassType 携带 TypeParams；TypeChecker 校验类型参数个数不匹配时报错

## 阶段 3: IrGen + 二进制格式 ✅

- [x] 3.1 `IrModule.cs`: IrFunction + IrClassDesc 新增 `TypeParams: List<string>?`
- [x] 3.2 `IrGen.cs`: 泛型类字段类型按共享 Ref 处理 + TypeParams 传递
- [x] 3.3 `ZbcWriter.cs`: SIGS section 写入 tp_count + names；TYPE section 类追加 tp_count + names
- [x] 3.4 `ZbcReader.cs`: 读取 SIGS + TYPE 的 type_params，装入 IrFunction/IrClassDesc
- [x] 3.5 `ZasmWriter.cs`: 类输出 `.type_params T, K, V`
- [x] 3.6 `ZpkgReader.cs`: SIGS 读取跳过 tp_count 字段（避免旧包读取错位）
- [x] 3.7 `dotnet build` 全绿

## 阶段 4: VM ✅

- [x] 4.1 `bytecode.rs`: `ClassDesc.type_params` + `Function.type_params` 字段
- [x] 4.2 `zbc_reader.rs`: 读取 SIGS + TYPE 中的 type_params 至 Function/ClassDesc
- [x] 4.3 `binary.rs`: `decode_type_section` 解码 tp_count + names；`assemble_module` 初始化空 type_params
- [x] 4.4 `loader.rs`: `build_type_registry` 把 ClassDesc.type_params 复制到 TypeDesc.type_params
- [x] 4.5 `merge_tests.rs`: 更新测试 fixture 带 `type_params: vec![]`
- [x] 4.6 `cargo build` 全绿

## 阶段 5: 测试与验证 ✅

- [x] 5.1 Golden test `68_generic_function` — 泛型函数定义 + 调用
- [x] 5.2 Golden test `69_generic_class` — Box<T>/Pair<A,B> 推断 + 显式类型参数 (`new Box<int>(...)`)
- [x] 5.3 ZbcRoundTripTests: `TypeParams_OnGenericClass_SurvivesBinaryRoundTrip` + `TypeParams_OnGenericFunction_SurvivesBinaryRoundTrip`
- [x] 5.4 TypeCheckerTests: 4 个用例（推断/显式/数量错误/泛型函数）
- [x] 5.5 `dotnet test`: 466/466 ✅（含 2 个新 round-trip + 4 个新 typechecker）
- [x] 5.6 `./scripts/test-vm.sh`: 132/132 ✅（interp + jit 各 66 个）
- [x] 5.7 docs/design/generics.md + docs/roadmap.md 更新（L3-G1 进度表）
- [x] 5.8 `scripts/regen-golden-tests.sh` 修正 driver DLL 路径（z42.Driver.dll → z42c.dll）

## 备注

- `<T>` 解析歧义：声明上下文（函数名后、类名后、new 后）解析为泛型，表达式中 `<` 仍为比较
- 泛型参数 T 在 IR 中映射为 object（通用 Value dispatch），不生成特化代码
- TypeDesc.type_args 字段已保留但 L3-G1 不填充（VM 不区分 `Box<int>` 与 `Box<string>` 的 TypeDesc）；L3-G2/G3 引入反射和 typeof 时再启用
- 本次不含 where 约束（L3-G2）
- **Scope 外发现并修复**：stdlib 二进制格式变更后 `regen-golden-tests.sh` 路径失效，顺手修正（纯脚本，未影响本变更逻辑）
