# Tasks: L3-G2.5 裸类型参数约束（`where U: T`）

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: TypeChecker 识别 + 体内成员查找 ✅
- [x] 阶段 2: 调用点子类型校验 ✅
- [x] 阶段 3: IR + zbc 格式扩展（0.5 → 0.6） ✅
- [x] 阶段 4: Rust VM 读取 ✅
- [x] 阶段 5: 测试 + 全量重生成 ✅
- [x] 阶段 6: 文档 + 验证 ✅

## 阶段 1: TypeChecker 识别 + 体内成员查找 ✅

- [x] 1.1 `Z42Type.cs`: `GenericConstraintBundle.TypeParamConstraint: string?`
- [x] 1.2 `TypeChecker.cs` `ResolveWhereConstraints`：先查 NamedType 是否命中 active type params
- [x] 1.3 多裸约束防护："cannot have multiple type-param constraints"
- [x] 1.4 `SymbolTable.LookupEffectiveConstraints`：一跳合并（U → T）
- [x] 1.5 `TypeChecker.Exprs.cs` BindMemberExpr + `TypeChecker.Calls.cs` 方法调用路径使用 effective bundle
- [x] 1.6 隐式上转：`IsAssignableTo` 已对 Z42GenericParamType 宽松（现有机制复用）
- [x] 1.7 `dotnet build` 全绿；无 L3-G2/G2.5 既有测试回归

## 阶段 2: 调用点子类型校验 ✅

- [x] 2.1 `ValidateGenericConstraints`：typeArg 映射后校验 `typeArg[U]` 是 `typeArg[T]` 的子类型
- [x] 2.2 `TypeArgSubsumedBy` helper（Z42ClassType 走 IsSubclassOf，其他相等）
- [x] 2.3 错误信息：`type argument X for U is not a subtype of Y (required by U: T on DeclName)`

## 阶段 3: IR + zbc 格式扩展 ✅

- [x] 3.1 `IrModule.cs`: `IrConstraintBundle.TypeParamConstraint: string?`
- [x] 3.2 `IrGen.BuildConstraintList` 拷贝新字段
- [x] 3.3 `ZbcWriter.cs`: VersionMinor 5 → 6；flag bit3 + 条件 name_idx
- [x] 3.4 `ZbcReader.cs`: 解码 bit3 + name
- [x] 3.5 `ZasmWriter.cs`: `.constraint U: T` 输出
- [x] 3.6 `ZpkgReader.cs`: SIGS 扫描跳过 bit3 字段
- [x] 3.7 `InternConstraintBundle` 补 TypeParamConstraint 字符串

## 阶段 4: Rust VM 读取 ✅

- [x] 4.1 `bytecode.rs`: `ConstraintBundle.type_param_constraint: Option<String>`
- [x] 4.2 `zbc_reader.rs` + `binary.rs`: 解码 bit3
- [x] 4.3 `loader.rs` `verify_constraints`：裸 type-param 引用无需校验（本地解），天然放行
- [x] 4.4 `cargo build` + `cargo test --lib metadata` 全绿

## 阶段 5: 测试 + 全量重生成 ✅

- [x] 5.1 `TypeCheckerTests.cs`: 5 新用例（SubclassArg_Ok / SiblingArg_Error / SameArg_Ok / InClass_ReturnAssign_Ok / MultipleBare_Error）
- [x] 5.2 `ZbcRoundTripTests.cs`: `Constraints_BareTypeParam_SurvivesRoundTrip`
- [x] 5.3 `constraint_tests.rs`: `loader_preserves_type_param_constraint`
- [x] 5.4 Golden `run/72_generic_bare_typeparam/`（Container<Animal, Dog> 实例化）
- [x] 5.5 Error golden `errors/33_bare_typeparam_not_subtype/`
- [x] 5.6 `./scripts/build-stdlib.sh`：stdlib zpkg 版本 0.6
- [x] 5.7 `./scripts/regen-golden-tests.sh`：71 golden source.zbc 重编
- [x] 5.8 `dotnet test` 508/508 ✅
- [x] 5.9 `cargo test --lib` 53/53 ✅
- [x] 5.10 `./scripts/test-vm.sh` 138/138 ✅（interp 69 + jit 69）

## 阶段 6: 文档 + 验证 ✅

- [x] 6.1 `docs/design/generics.md`: 裸类型参数约束小节
- [x] 6.2 `docs/roadmap.md`: 范式表裸参数 → ✅

## 备注

- 一跳策略：`U: V, V: T` 两跳不支持；实际用例稀少，显式报错/默认无效
- primitive / interface 作 typeArg[T] 时回退为相等性（不做 primitive 子类型）
- **z42 函数调用暂不支持显式 type args**（仅 `new Container<T>(...)` 支持）— 测试用例均基于泛型类实例化；函数 call-site 需推断
- Golden run/72 简化为仅 ObjNew + 字段访问（method-call 返回 T 的 call-site 替换未实现，是 L3-G3b / L3-R 范围）

## Scope 外发现

- 无（一次干净落地）
