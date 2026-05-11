# Tasks: add-generic-func-constraint

> 状态：🟢 已完成 (2026-05-11) | 创建：2026-05-11 | 类型：lang（完整流程）

## 进度概览

- [ ] Phase 1: TypeChecker — 约束 bundle 字段 + resolve + validate
- [ ] Phase 2: IrGen + zbc writer/reader（zbc 1.3→1.4 / zpkg 0.4→0.5）
- [ ] Phase 3: Rust VM loader + verify_constraints
- [ ] Phase 4: C# 单元测试 + golden tests
- [ ] Phase 5: 错误码 catalog（E0408 / E0409）
- [ ] Phase 6: docs/design/language/generics.md 同步 + roadmap Deferred 移除
- [ ] Phase 7: GREEN 验证 + commit + push

## Phase 1: TypeChecker

- [ ] 1.1 `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs`：`GenericConstraintBundle` 加 `FuncSignature: Z42FuncType?` 字段（初值 null，向后兼容）
- [ ] 1.2 `src/compiler/z42.Semantics/TypeCheck/TypeChecker.GenericResolve.cs`：`ResolveWhereConstraints()` 中识别 TypeExpr resolve 后是 `Z42FuncType` 的情况（之前会走 interface 分支报错）；route 到 bundle.FuncSignature
- [ ] 1.3 `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs`：`ValidateGenericConstraints()` 入口先做 v1 组合 reject 检查（FuncSignature != null && (其他字段非空)）→ E0409；然后用 `bundle.FuncSignature.IsAssignableFrom(actualTypeArg)` 检查 → E0408
- [ ] 1.4 `TypeChecker.Exprs.cs::BindCallExpr`：receiver 类型为 `Z42GenericParamType` 且约束 bundle 有 FuncSignature → 推断结果类型为 FuncSignature.Ret，发射 `BoundCallIndirect`
- [ ] 1.5 单元测试：`Z42TypeTests.cs` 加 GenericConstraintBundle.FuncSignature equality / variance 单测

## Phase 2: IrGen + zbc

- [ ] 2.1 `src/compiler/z42.IR/IrModule.cs`：`IrConstraintBundle` record 加 `FuncSignature: IrFuncSig?` 字段；新增 `IrFuncSig { Params: List<TypeTag>, Ret: TypeTag }` record
- [ ] 2.2 `src/compiler/z42.Semantics/Codegen/IrGen.cs::EmitConstraintBundle`：发射 FuncSignature（从 Z42FuncType 转 IrFuncSig）
- [ ] 2.3 `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs`：SIGS section per-tp constraint flags 加 bit 0x20；该 bit 置位时后写 param_count:u8 + per-param TypeTag + return TypeTag
- [ ] 2.4 `ZbcWriter.cs::VersionMinor` 1.3 → 1.4，加注释说明 split-debug-symbols 之后又一次扩展
- [ ] 2.5 `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs`：对称读取；min 版本号校验 → 1.4+ 才读 func sig
- [ ] 2.6 `src/compiler/z42.Project/ZpkgWriter.cs` + `ZpkgReader.cs`：minor 0.4 → 0.5
- [ ] 2.7 stdlib regen：`./scripts/build-stdlib.sh` 全部重生（zpkg 0.5）

## Phase 3: Rust VM

- [ ] 3.1 `src/runtime/src/metadata/types.rs`：`TypeParamConstraint` 加 `func_signature: Option<FuncSigDescriptor>`；新增 `FuncSigDescriptor { params: Vec<TypeTag>, ret: TypeTag }`
- [ ] 3.2 `src/runtime/src/metadata/zbc_reader.rs`：constraint bundle 解码加 flag 0x20 分支
- [ ] 3.3 `src/runtime/src/metadata/loader.rs::verify_constraints`：遍历 func_signature 内引用的 class/interface 类型 → look up type_registry → 不存在 fatal（保持与既有 base_class/interfaces 校验一致）
- [ ] 3.4 Rust 单元测试：`src/runtime/src/metadata/constraint_tests.rs` 加 func sig encode/decode round-trip + 类型不存在 fatal 测试

## Phase 4: 测试

- [ ] 4.1 `src/compiler/z42.Tests/GenericFuncConstraintTests.cs`：parser 等价（命名 ↔ 字面量）/ variance 4 组合 / E0408 / E0409 单测
- [ ] 4.2 `src/tests/generics/func_constraint_basic/`：基础 Func<int,int> 调用 + 数值断言
- [ ] 4.3 `src/tests/generics/func_constraint_action/`：Action<EventArgs> + 副作用
- [ ] 4.4 `src/tests/generics/func_constraint_variance/`：Cat/Animal 链 + 4 variance 组合
- [ ] 4.5 `src/tests/generics/func_constraint_literal/`：`(int) -> int` 字面量等价
- [ ] 4.6 `src/tests/errors/E0408_func_constraint_violation/source.z42` + `expected.txt`

## Phase 5: 错误码

- [ ] 5.1 `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs`：
  - E0408 GenericFuncConstraintViolation（带 期望 sig + 实际 sig）
  - E0409 InvalidFuncConstraint（v1 组合禁止 + 修复建议）
- [ ] 5.2 `RustErrorCatalog.cs`：无 Z### 新增（runtime 端 verify_constraints 复用既有 Z0### 类型不存在错）

## Phase 6: 文档同步

- [ ] 6.1 `docs/design/language/generics.md`：新增 §"委托/函数约束"小节，包含：
  - 双语法形态（命名 + 字面量）
  - variance 规则（与 function value 赋值一致）
  - v1 组合限制
  - body 调用 desugar 到 CallIndirect
  - zbc 编码（flag 0x20）
- [ ] 6.2 `docs/design/language/generics.md` L3-G2.5 表更新："委托/函数约束" 行从 🟠 低 改 ✅ 已完成
- [ ] 6.3 `docs/roadmap.md`：Deferred Backlog Index 中无该条目（确认），不动；若有提及处更新状态
- [ ] 6.4 `docs/design/runtime/zbc.md`：SIGS section 文档加 flag 0x20 + payload 描述
- [ ] 6.5 `docs/design/runtime/ir.md`：无新指令，加注释说明 CallIndirect 现可用于"约束驱动的泛型 callable 参数调用"

## Phase 7: GREEN 验证 + commit

- [ ] 7.1 `dotnet build src/compiler/z42.slnx`
- [ ] 7.2 `cargo build --manifest-path src/runtime/Cargo.toml`
- [ ] 7.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [ ] 7.4 `./scripts/build-stdlib.sh` —— stdlib 重生（zpkg 0.5）
- [ ] 7.5 `./scripts/test-vm.sh` —— interp + JIT 双模全绿
- [ ] 7.6 `./scripts/test-stdlib.sh` —— 6 个 lib 测试
- [ ] 7.7 `./scripts/test-cross-zpkg.sh`
- [ ] 7.8 Spec scenario 覆盖确认（spec.md 中每条 scenario 逐条勾选）
- [ ] 7.9 commit + push（type: feat / scope: generics / 描述: add func-type constraint）
- [ ] 7.10 归档：`docs/spec/changes/add-generic-func-constraint/` → `docs/spec/archive/2026-05-11-add-generic-func-constraint/`

## 备注

- zbc / zpkg 同时 bump 与既有 split-debug-symbols Phase 4 并存：注意 push 前 rebase / 协调 minor 号
- Variance 实现完全复用 Z42FuncType.AssignableTo，无新规则
- IR / VM / JIT 0 新指令；运行时 only verify_constraints 加一行
- 若实施期发现 ref/out/in 修饰符匹配语义模糊 → 停 + 汇报 + 等 User 裁决（按 workflow rule）
