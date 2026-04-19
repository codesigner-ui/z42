# Tasks: L3-G1 泛型基础（泛型函数 + 泛型类，无约束）

> 状态：🟡 进行中 | 创建：2026-04-19

## 进度概览
- [ ] 阶段 1: AST + Parser
- [ ] 阶段 2: TypeChecker
- [ ] 阶段 3: IrGen + 二进制格式
- [ ] 阶段 4: VM
- [ ] 阶段 5: 测试与验证

## 阶段 1: AST + Parser

- [ ] 1.1 `Ast.cs`: FunctionDecl 新增 `List<string>? TypeParams = null`
- [ ] 1.2 `Ast.cs`: ClassDecl 新增 `List<string>? TypeParams = null`
- [ ] 1.3 `Ast.cs`: InterfaceDecl 新增 `List<string>? TypeParams = null`
- [ ] 1.4 `Ast.cs`: 新增 `GenericType(string Name, List<TypeExpr> TypeArgs, Span)` TypeExpr
- [ ] 1.5 `TopLevelParser.Helpers.cs`: `SkipGenericParams` → `ParseTypeParams` 返回 `List<string>?`
- [ ] 1.6 `TopLevelParser.cs`: ParseClassDecl/ParseInterfaceDecl 使用 ParseTypeParams
- [ ] 1.7 `TopLevelParser.cs`: ParseFunctionDecl 检测 `Ident<` 解析类型参数
- [ ] 1.8 `TypeParser.cs`: 解析 `Name<TypeArg1, TypeArg2>` → `GenericType`
- [ ] 1.9 `dotnet build` 全绿

## 阶段 2: TypeChecker

- [ ] 2.1 新增 `Z42GenericParamType` 类型（代表未实例化的 T）
- [ ] 2.2 泛型参数作用域：check 类/函数时 push TypeParam → scope，结束 pop
- [ ] 2.3 泛型函数调用：`Identity<int>(42)` → 替换 T=int 验证参数
- [ ] 2.4 泛型类实例化：`new Box<int>(42)` → 替换 T=int 验证构造器
- [ ] 2.5 泛型类方法调用：`b.Get()` → 返回类型根据 type_args 解析
- [ ] 2.6 `GenericType` TypeExpr 解析为具体的 Z42ClassType + type_args
- [ ] 2.7 `dotnet test` 全绿

## 阶段 3: IrGen + 二进制格式

- [ ] 3.1 `IrModule.cs`: IrFunction 新增 `TypeParams: List<string>?`
- [ ] 3.2 `FunctionEmitter.cs`: 泛型参数 T → IrType.Ref（代码共享）
- [ ] 3.3 `ZbcWriter.cs`: SIGS section 写入 type_param_count + names
- [ ] 3.4 `ZbcReader.cs`: 读取 type_params
- [ ] 3.5 `ZasmWriter.cs`: 输出 `.type_params T, K, V`
- [ ] 3.6 `dotnet build` 全绿

## 阶段 4: VM

- [ ] 4.1 `types.rs`: TypeDesc 新增 `type_params: Vec<String>`, `type_args: Vec<String>`
- [ ] 4.2 `zbc_reader.rs`: 读取 SIGS 中的 type_params
- [ ] 4.3 `loader.rs`: build_type_registry 处理泛型类名（如 `Box<int>`）
- [ ] 4.4 `exec_instr.rs`: ObjNew 解析泛型类名 → 实例化 TypeDesc
- [ ] 4.5 `cargo build` 全绿

## 阶段 5: 测试与验证

- [ ] 5.1 Golden test `68_generic_function` — 泛型函数定义 + 调用
- [ ] 5.2 Golden test `69_generic_class` — 泛型类定义 + 实例化 + 方法
- [ ] 5.3 ZbcRoundTrip: type_params 序列化 round-trip
- [ ] 5.4 `dotnet test` 全绿
- [ ] 5.5 `./scripts/test-vm.sh` 全绿
- [ ] 5.6 docs/design/generics.md + docs/roadmap.md 更新

## 备注

- `<T>` 解析歧义：声明上下文（函数名后、类名后、new 后）解析为泛型，表达式中 `<` 仍为比较
- 泛型参数 T 在 IR 中映射为 IrType.Ref — 不生成特化代码
- 本次不含 where 约束（L3-G2）
