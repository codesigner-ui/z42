# Tasks: add-default-generic-typeparam (D-8b-3 Phase 2)

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：feat(lang+ir+vm)

**变更说明**：泛型 type-param `default(T)` 运行时解析（D-8b-3 Phase 2）。

## 进度概览

- [x] 阶段 1: IR opcode + 编/解码 + 文本格式
- [x] 阶段 2: TypeChecker 解 gate + IrGen emit
- [x] 阶段 3: VM interp DefaultOf dispatch
- [x] 阶段 3.5: **type_args propagation**（实施期发现，User 裁决 A 扩入主 spec）
- [x] 阶段 4: 测试 + 文档同步
- [x] 阶段 5: 全绿验证 + 归档

## 阶段 1: IR opcode infrastructure（C# 端）

- [x] 1.1 Opcodes.cs 加 `DefaultOf = 0xB0`（新 0xB0–0xBF generic-runtime 段）
- [x] 1.2 IrModule.cs 加 `DefaultOfInstr(TypedReg Dst, byte ParamIndex)` record
- [x] 1.3 ZbcWriter.cs `VersionMinor` 0.8 → 0.9
- [x] 1.4 ZbcWriter.Instructions.cs 加编码 case + RegVisit 加 dst 访问
- [x] 1.5 ZbcReader.Instructions.cs 加解码 case
- [x] 1.6 ZasmWriter.cs 加文本格式 `%dst = default.of  $idx`
- [x] 1.7 IrVerifier.cs 加 GetDst 分支
- [x] 1.8 dotnet build 全绿

## 阶段 2: TypeChecker + IrGen（C# 端）

- [x] 2.1 BoundDefault record 加 nullable `GenericParamIndex` 字段
- [x] 2.2 TypeChecker.Exprs.cs case DefaultExpr：解除 `Z42GenericParamType` E0421 gate；新增 `ResolveGenericParamIndex(name, env)` helper（从 enclosing class TypeParams 查 idx，未命中返 0）
- [x] 2.3 FunctionEmitterExprs.cs case BoundDefault：`GenericParamIndex is int idx → emit DefaultOfInstr`；其他保留 Phase 1 Const* 路径
- [x] 2.4 dotnet build 全绿

## 阶段 3: VM interp DefaultOf dispatch（Rust 端）

- [x] 3.1 bytecode.rs `Instruction::DefaultOf { dst, param_index: u8 }` variant
- [x] 3.2 zbc_reader.rs OP_DEFAULT_OF (0xB0) 解码
- [x] 3.3 interp/exec_instr.rs DefaultOf 分支：`frame.regs[0] -> Object → instance.type_args.get(idx).map(default_value_for).unwrap_or(Null)`
- [x] 3.4 jit/translate.rs DefaultOf bail（与 LoadFieldAddr 同 pattern）
- [x] 3.5 jit/translate.rs max_reg branch 加 dst
- [x] 3.6 cargo build 全绿

## 阶段 3.5: type_args propagation（**实施期扩入**，User 裁决 A）

**背景**：阶段 4 第一次 golden 测试发现 `default(R) on Foo<int>` 返回 Null。Root cause：z42 走真正的 erasure，`type_desc.type_args` 永远为空，VM 无从得知 `<int>` 实例化。User 选 A：本 spec 同时实施 type_args propagation 基础设施。

- [x] 3.5.1 metadata/types.rs `ScriptObject` struct 加 `type_args: Vec<String>` 字段
- [x] 3.5.2 gc/rc_heap.rs `alloc_object` 默认初始化 `type_args: Vec::new()`（保持 trait 签名不变）
- [x] 3.5.3 bytecode.rs `Instruction::ObjNew` variant 加 `type_args: Vec<String>`（serde default 空）
- [x] 3.5.4 zbc_reader.rs OP_OBJ_NEW 末尾解码 type_args（`u8 count + N*u32 strings`）
- [x] 3.5.5 interp/exec_instr.rs ObjNew 分支：alloc 后 `if !type_args.is_empty() { borrow_mut().type_args = type_args.clone() }`
- [x] 3.5.6 interp DefaultOf 改读 `instance.type_args` 而非 `type_desc.type_args`
- [x] 3.5.7 jit/translate.rs ObjNew 分支添加 `type_args: _` 解构（不传给 helper，JIT 实例 type_args 为空）
- [x] 3.5.8 jit/translate.rs max_reg ObjNew 分支保持
- [x] 3.5.9 IrModule.cs `ObjNewInstr` 加 `IReadOnlyList<string>? TypeArgs = null`
- [x] 3.5.10 ZbcWriter.Instructions.cs ObjNew 编码末尾写 type_args（`u8 count + N*u32 idx`）+ intern strings
- [x] 3.5.11 ZbcReader.Instructions.cs ObjNew 解码 type_args
- [x] 3.5.12 FunctionEmitterExprs.cs `EmitBoundNew`: 当 `n.Type is Z42InstantiatedType inst` 时 `inst.TypeArgs.Select(TypeToString).ToList()` 传给 ObjNewInstr
- [x] 3.5.13 metadata/formats.rs ZBC_VERSION 0.7 → 0.9（同步 C# 端 bump）

## 阶段 4: 测试 + 文档同步

- [x] 4.1 src/tests/operators/default_generic_param/ golden — `class Foo<R>` method 内 default(R) 验证 int → 0、string → null
- [x] 4.2 src/tests/operators/default_generic_param_pair/ golden — `Pair<K, V>` 多 type-param 验证
- [x] 4.3 src/tests/operators/default_generic_param_field_init/ golden — 字段在 ctor body 内 default(T) init 验证
- [x] 4.4 上述 3 个 golden 加 `interp_only` 标记（JIT 路径 type_args 不传，default(T) 退化为 Null —— interp 是真值源）
- [x] 4.5 DefaultExpressionTests.cs：移除原 `TypeChecker_Default_GenericTypeParam_RaisesE0421`；新增 2 个 case 验证 GenericParamIndex 正确解析（class-level 0/1 位置）
- [x] 4.6 src/tests/errors/421_invalid_default_type/ 移除 generic-T case；保留 unknown type 单 case + 更新 expected_error.txt
- [x] 4.7 docs/design/language-overview.md §3 default(T) 段同步 Phase 2 支持 + graceful-degradation 边界
- [x] 4.8 docs/design/ir.md 加 `default_of` 指令文档 + obj_new 0.9 起 carry type_args 说明
- [x] 4.9 docs/deferred.md 把 D-8b-3 Phase 2 从 active 移到"已落地"段
- [x] 4.10 dotnet test 1097/1097 全绿
- [x] 4.11 cargo test 全绿（240 + 多个独立 testbinaries）
- [x] 4.12 ./scripts/test-vm.sh 293/293 全绿（150 interp + 143 jit；3 个新 generic 测试 interp-only 已 skip jit）

## 阶段 5: 归档 + commit

- [x] 5.1 commit + push
- [x] 5.2 spec → spec/archive/2026-05-07-add-default-generic-typeparam/

## 实施备注

### 阶段 3.5 的"实施期扩入"理由

阶段 4 第一次跑 `default_generic_param/` golden 时返回 null（应返 0）。调查发现：z42 VM 当前的 `TypeDesc.type_args` 字段永远是 `vec![]`（loader.rs:368），即设计期假设的"runtime 已携带 type_args"是错误的。本 spec 的核心承诺是 `Foo<int>::Make() → 0`，不修这个根因就交付不了。User 裁决 A：本 spec 同时实施 type_args propagation。Scope 扩展把 IR ObjNew + Rust ScriptObject struct 扩字段都纳入；规模放大约 1.5x，但保留单一 commit、单一回退点。

### graceful-degradation 边界

method-level type-param `m<U>()`、free generic function `f<T>()`、static method on generic class —— 这三种场景目前**编译通过、运行返 Null**。原因是 calling convention 不携带 type_args，callee 拿不到。后续 spec 处理（candidate name: `add-method-level-type-args`）。本 spec 把它们记入 docs/design/language-overview.md "graceful-degradation 边界"段。

### JIT 路径限制

JIT 编译的 generic-class 实例化（`new Foo<int>()` 在 JIT-compiled 函数内）目前 type_args 为空，因 `jit_obj_new` 签名未扩展。JIT 内 `default(T)` 也走 bail（同 LoadFieldAddr）。3 个新 golden 加 `interp_only` 标记。Follow-up：扩展 jit_obj_new 签名 + 实现 jit_default_of helper。

### Out of Scope（保留 follow-up）

- 真正的方法级 type-param（`class Foo { void m<U>() {...} }`）的 type_args 计参 calling convention
- 真正的 free generic function `void f<T>()` 的 type_args 携带
- Static method on generic class（无 `this`）的 type_args 解析
- JIT helper `jit_default_of` + 扩展 `jit_obj_new` 携带 type_args
- `typeof(T)` / runtime reflection API
- monomorphization
