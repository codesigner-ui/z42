# Tasks: L3-G2 泛型约束（where T: I + J）

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: Parser + AST ✅
- [x] 阶段 2: TypeChecker ✅
- [x] 阶段 3: 标准库 + 测试 ✅
- [x] 阶段 4: 文档 + 验证 ✅

## 阶段 1: Parser + AST ✅

- [x] 1.1 `TokenKind.cs` / `TokenDefs.cs`: 新增 `Where` keyword（此前未注册）
- [x] 1.2 `Ast.cs`: 新增 `WhereClause` + `GenericConstraint` records
- [x] 1.3 `Ast.cs`: FunctionDecl / ClassDecl / InterfaceDecl 追加 `WhereClause? Where = null`
- [x] 1.4 `TopLevelParser.Helpers.cs`: 新增 `ParseWhereClause`（支持 `T: I + J` / `K: I, V: J`）
- [x] 1.5 `TopLevelParser.cs`: ParseFunctionDecl 在 `{`/`=>` 前解析 where
- [x] 1.6 `TopLevelParser.cs`: ParseClassDecl / ParseInterfaceDecl 在 base/interface 后解析 where
- [x] 1.7 `dotnet build` 全绿

## 阶段 2: TypeChecker ✅

- [x] 2.1 `Z42Type.cs`: `Z42GenericParamType` 新增 `Constraints: IReadOnlyList<Z42InterfaceType>?`
- [x] 2.2 `SymbolTable.cs`: `PushTypeParams(typeParams, constraints?)` 可选约束映射；`LookupActiveTypeParamConstraints`
- [x] 2.3 `TypeChecker.cs`: `ResolveAllWhereConstraints` Pass 0.5 解析所有 where → 缓存 `_funcConstraints` / `_classConstraints`
- [x] 2.4 `TypeChecker.Exprs.cs` (BindMemberExpr): 接收者为 `Z42GenericParamType` 时，约束接口中查找方法；未约束或找不到 → E0402
- [x] 2.5 `TypeChecker.Calls.cs`: 同上 for 方法调用；受接收者字段存储时 Constraints=null 影响 → 通过 `_symbols.LookupActiveTypeParamConstraints` 兜底
- [x] 2.6 `TypeChecker.Calls.cs`: `InferAndValidateFuncConstraints` + `SubstituteGenericReturn`（返回类型 T → 具体类型替换）
- [x] 2.7 `TypeChecker.Exprs.cs` (BindNew): `new GenericClass<T>(...)` 校验每个类型参数实现所有约束
- [x] 2.8 `ValidateGenericConstraints` + `TypeSatisfiesInterface` 通用助手
- [x] 2.9 `dotnet build` 全绿

## 阶段 3: 标准库 + 测试 ✅

- [x] 3.1 `z42.core/src/IComparable.z42`: 启用 `interface IComparable<T> { int CompareTo(T other); }`
- [x] 3.2 `z42.core/src/IEquatable.z42`: 启用 `interface IEquatable<T> { bool Equals(T other); }`
- [x] 3.3 `TypeCheckerTests.cs`: 8 个新用例（TC1-TC8）
- [x] 3.4 Golden test `70_generic_constraints/`: Num + IComparable + Max<Num> 跑通 interp + jit
- [x] 3.5 Error golden `errors/26_generic_constraint_not_satisfied` + `errors/27_generic_member_on_unconstrained_t`
- [x] 3.6 `./scripts/build-stdlib.sh` 重编 stdlib
- [x] 3.7 `./scripts/regen-golden-tests.sh` 更新 source.zbc

## 阶段 4: 文档 + 验证 ✅

- [x] 4.1 `docs/design/generics.md`: L3-G2 落地细节小节（语法、语义、限制、L3-G3 必做项）
- [x] 4.2 `docs/roadmap.md`: L3-G 进度表 G2 → ✅
- [x] 4.3 `dotnet build src/compiler/z42.slnx` 无错误
- [x] 4.4 `cargo build --manifest-path src/runtime/Cargo.toml` 无错误
- [x] 4.5 `dotnet test`: 475/475 ✅（包含 8 新泛型约束用例 + 2 error golden + 1 golden run）
- [x] 4.6 `./scripts/test-vm.sh`: 134/134 ✅（interp 67 + jit 67）
- [x] 4.7 Spec scenarios 全部覆盖（见下表）

### Spec 覆盖矩阵

| Scenario | 实现位置 | 验证方式 |
|---|---|---|
| 单一接口约束 | TypeChecker.cs:ResolveAllWhereConstraints | TC1 Generic_SingleConstraint_MethodCallOk |
| 多接口约束 `+` | TopLevelParser.Helpers.cs:ParseWhereClause | TC2 Generic_MultiConstraint_BothMethodsOk |
| 跨类型参数约束 `,` | 同上 | TC3 Generic_CrossParamConstraint_Ok |
| 泛型类 where | ParseClassDecl | TC4, TC7 |
| 约束接口方法可调用 | TypeChecker.Calls.cs:recvExpr Z42GenericParamType | TC1, golden run/70 |
| 未约束方法调用报错 | TypeChecker.Calls.cs:Z42GenericParamType else-path | TC6, errors/27 |
| 类型参数满足约束（类实例化） | TypeChecker.Exprs.cs:BindNew | TC4 Generic_CallSite_TypeArgImplements_Ok |
| 类型参数不满足约束 | ValidateGenericConstraints | TC5, errors/26 |
| 类型推断 + 约束校验 | InferAndValidateFuncConstraints | TC8 Generic_Inferred_ConstraintSatisfied_Ok, golden run/70 |
| 类字段上调用约束方法 | SymbolTable.LookupActiveTypeParamConstraints | TC7 Generic_ClassField_ConstraintMethodCall_Ok |

## 备注

- 发现 `where` 关键字未注册到 TokenDefs — 本次补上（脚手架遗漏，非本需求新增）
- 顺带修正：ClassDecl 基类/接口列表第一个名字未跳过 `<...>`，补上 `SkipGenericParams` 保持一致（Scope 内小修复）
- 意外收获：`SubstituteGenericReturn` — 泛型函数调用的返回类型按实参推断替换 T → 具体类型（原本 L3-G1 未做，补上才能让 `Max(Num, Num).value` 合法）

## 后续阶段（L3-G3）必做项 — 用户明确要求记录

- [ ] **zbc 二进制扩展**：SIGS / TYPE / TSIG section 追加约束字段
- [ ] **VM loader**：读取约束到 `TypeDesc.constraints` / `Function.constraints`
- [ ] **VM 运行时校验**：ObjNew / 泛型函数 Call 时校验 type_args 实现约束
- [ ] **反射接口**：`type.Constraints` / `t is IComparable<T>` 运行时判断
- [ ] **跨 zpkg TypeChecker**：外部依赖泛型签名携带约束
