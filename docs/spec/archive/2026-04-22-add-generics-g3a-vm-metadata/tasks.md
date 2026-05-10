# Tasks: L3-G3a — VM 约束元数据 + 加载时校验

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: IR 层 + IrGen 暴露约束 ✅
- [x] 阶段 2: zbc Writer / Reader / ZpkgReader ✅
- [x] 阶段 3: Rust VM bytecode + loader + verify pass ✅
- [x] 阶段 4: 测试 + zbc 全量重生成 ✅
- [x] 阶段 5: 文档 + 验证 ✅

## 阶段 1: IR 层 + IrGen 暴露约束 ✅

- [x] 1.1 `IrModule.cs`: 新增 `IrConstraintBundle(RequiresClass, RequiresStruct, BaseClass?, Interfaces)` record
- [x] 1.2 `IrFunction` / `IrClassDesc` 加 `TypeParamConstraints: List<IrConstraintBundle>?`
- [x] 1.3 `SemanticModel` 暴露 `FuncConstraints` / `ClassConstraints`（来自 TypeChecker `_funcConstraints` / `_classConstraints`）
- [x] 1.4 `IrGen.BuildConstraintList` helper
- [x] 1.5 `FunctionEmitter` 方法 / 自由函数构造 IrFunction 时填充 `TypeParamConstraints`
- [x] 1.6 `IrGen.EmitClassDesc` 填充 `TypeParamConstraints`
- [x] 1.7 `dotnet build` 全绿

## 阶段 2: zbc Writer / Reader / ZpkgReader ✅

- [x] 2.1 `ZbcWriter.cs`: VersionMinor 4 → 5
- [x] 2.2 `ZbcWriter.cs`: `WriteConstraintBundle` + `InternConstraintBundle` helpers
- [x] 2.3 `ZbcWriter.cs`: SIGS 每 tp 写入约束；TYPE 同；InternPoolStrings 扩展
- [x] 2.4 `ZbcReader.cs`: `ReadConstraintBundle` + SigEntry.TypeParamConstraints + TYPE 读取
- [x] 2.5 `ZasmWriter.cs`: `.constraint T: X + Y + class` 可读输出
- [x] 2.6 `ZpkgReader.cs`: ReadSigsSection 同步跳过新字段

## 阶段 3: Rust VM bytecode + loader + verify pass ✅

- [x] 3.1 `bytecode.rs`: `ConstraintBundle` struct
- [x] 3.2 `bytecode.rs`: `Function` + `ClassDesc` 加 `type_param_constraints: Vec<ConstraintBundle>`
- [x] 3.3 `zbc_reader.rs`: SIGS / TYPE 解码 `read_constraint_bundle` + SigEntry
- [x] 3.4 `binary.rs`: `decode_constraint_bundle` + `decode_type_section` 同步
- [x] 3.5 `types.rs`: `TypeDesc.type_param_constraints` 字段；全仓 TypeDesc 构造点补字段（4 处：loader, dispatch, jit helpers, corelib/object, corelib/string_builder, corelib/tests）
- [x] 3.6 `loader.rs`: `build_type_registry` 拷贝 ClassDesc 约束到 TypeDesc
- [x] 3.7 `loader.rs`: `verify_constraints` pass；`load_zbc` / `load_zpkg` / `main.rs` 加载路径调用
- [x] 3.8 `main.rs`: 调用 `verify_constraints`
- [x] 3.9 `merge_tests.rs`: fixture 更新（Function/ClassDesc 补字段）
- [x] 3.10 `cargo build` + `cargo test --lib metadata` 全绿

## 阶段 4: 测试 + zbc 全量重生成 ✅

- [x] 4.1 `ZbcRoundTripTests.cs`: 4 新 round-trip 测试（Interface / BaseClass / ClassFlag / GenericClassBaseAndInterface）
- [x] 4.2 `src/runtime/src/metadata/constraint_tests.rs`: 5 新测试（populates / accepts / rejects unknown / std namespace / interface-like name）
- [x] 4.3 `./scripts/build-stdlib.sh`：stdlib zpkg 重编（格式 0.5）
- [x] 4.4 `./scripts/regen-golden-tests.sh`：70 个 golden source.zbc 重编
- [x] 4.5 `dotnet test`: 500/500 ✅（496 + 4 新 round-trip）
- [x] 4.6 `./scripts/test-vm.sh`: 136/136 ✅（interp 68 + jit 68）

## 阶段 5: 文档 + 验证 ✅

- [x] 5.1 `docs/design/generics.md`: L3-G3a 落地细节 + L3-G3 剩余子阶段
- [x] 5.2 `docs/roadmap.md`: L3-G3a → ✅；L3-G3b 范围更新（含运行时校验）
- [x] 5.3 全绿验证：dotnet build + cargo build + dotnet test + cargo test + test-vm.sh

## 备注

- 本次**不**实现运行时 Call/ObjNew 校验（代码共享下 type_args 不可得，留给 L3-G3b 配合反射机制）
- TSIG section 约束扩展留给 L3-G3d 跨 zpkg TypeChecker
- Soft-allow 规则：verify_constraints 对 `Std.*` 前缀放行（lazy loader 兜底），对 `I<Upper>...` 接口名放行（L3-G3b 反射前无独立 interface 注册）— 这让当前 zbc 无回归又保留校验能力

## Scope 外发现记录

- `lazy_loader_tests.rs` 用 `mod lazy_loader_tests;` 声明，Rust 默认路径规则找不到 → `#[path = "lazy_loader_tests.rs"]` 修正（pre-existing，阻塞 `cargo test`，顺手补）
