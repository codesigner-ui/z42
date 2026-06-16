# Tasks: typeof 携带泛型实例化 args

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：ir（新 opcode + zbc/zpkg bump，完整流程）

## 进度概览
- [ ] 阶段 1: IR + wire（新 opcode + version bump）
- [ ] 阶段 2: codegen
- [ ] 阶段 3: runtime（reader + interp + jit + reflection）
- [ ] 阶段 4: stdlib
- [ ] 阶段 5: 测试与验证
- [ ] 阶段 6: docs + 归档

## 阶段 1: IR + wire
- [x] 1.1 `Opcodes.cs`：`Typeof = 0x73`
- [x] 1.2 `IrModule.cs`：`TypeofInstr(TypedReg Dst, string TypeName, IReadOnlyList<string> TypeArgs)` record
- [x] 1.3 `ZbcWriter.Instructions.cs`：emit + intern + reg-visit；`ZbcReader.Instructions.cs` round-trip；`IrVerifier`（Dst）；`ZasmWriter`（text）
- [x] 1.4 `ZbcWriter.cs`：`VersionMinor` 17→18 + 注释
- [x] 1.5 `ZpkgWriter.cs`：`VersionMinor` 19→20 + 注释

## 阶段 2: codegen
- [x] 2.1 `FunctionEmitterExprs.cs`：`VisitTypeof` emit `TypeofInstr`（定义名 + Z42TypeName 化 TypeArgs）
- [x] 2.2 `Z42TypeName(Z42InstantiatedType)` 仍只产定义名（args 走 instr 字段）
- [x] 2.3 `__typeof` builtin codegen 路径已移除（VisitTypeof 不再 emit ConstStr+Builtin）
- [x] 2.4 dotnet build 编译器：0 error

## 阶段 3: runtime
- [x] 3.1 `zbc_reader.rs`：`ZBC_VERSION_MINOR` 18 / `ZPKG_VERSION_MINOR` 20
- [x] 3.2 `zbc_reader.rs`：读 Typeof opcode → `TypeofInsn`（+ OP_TYPEOF 0x73 + import）
- [x] 3.3 `bytecode.rs` + `metadata/mod.rs`：`Instruction::Typeof` 变体 + `TypeofInsn` 结构 + re-export
- [x] 3.4 `interp/exec_instr.rs`：求值 Typeof（make_constructed_type → frame.set）
- [x] 3.5 `jit/`：`jit_typeof` helper（object.rs）+ registry decl/reg + translate case + dst-switch
- [x] 3.6 `reflection.rs`：`make_constructed_type` 挂 `__typeArgs` 槽；`__type_is_generic_definition` / `__type_generic_definition` builtin；`builtin_type_generic_args` 优先读槽
- [x] 3.7 `corelib/mod.rs`：注册 2 新 builtin；移除 `__typeof`（typeof 走 opcode）
- [x] 3.8 cargo build release：0 error

## 阶段 4: stdlib
- [x] 4.1 `Type.z42`：`IsGenericTypeDefinition` + `GetGenericTypeDefinition()` extern；`__typeArgs` 字段；`GetGenericArguments`/`IsGenericType` 文档更新

## 阶段 5: 测试与验证
- [x] 5.1 C# round-trip：ZbcReader.Instructions Typeof + ReadWriteRoundTrip（dotnet 1564/1564 覆盖）
- [x] 5.2 golden `src/tests/types/generic_type_definition.z42`（interp+jit，含非泛型抛异常 try/catch）
- [x] 5.3 `src/tests/zbc-format/generate-fixtures.sh` regen（6/6）
- [x] 5.4 `src/tests/zpkg-format/generate-fixtures.sh` regen（12 pass）
- [x] 5.5 `dotnet test` 全绿 1564/1564（含 Zbc/Zpkg invariant + GoldenTests）
- [x] 5.6 `cargo build`（debug+release）+ `cargo test --lib` 808/0（+ version-pin 18/20）
- [x] 5.7 xtask vm 360/0（interp 184 + jit 176）/ cross-zpkg 2/0 / stdlib 272·22（regen 后）

## 阶段 5 备注：根因修（Scope 扩展，runtime 锁内）
- `GetGenericTypeDefinition` 在非泛型上抛异常，**interp 捕获正常但 JIT uncaught**。
  根因：`jit_builtin`（call.rs）把 builtin 错误设为原始 `Value::Str`，`catch (Exception)`
  不匹配 → make-corelib-errors-catchable（2026-05-15）只修了 interp 路径，JIT 留 latent bug。
  本 change 是首个可抛的 reflection builtin，首次触发。
- 修复：`jit_builtin` 镜像 interp，用 `make_stdlib_exception` 包装成 `Std.Exception`。
  回归测试 = golden 的 jit 模式（非泛型 try/catch 分支）。

## 阶段 6: docs + 归档
- [x] 6.1 `reflection.md`：构造型泛型主体节 + Deferred（generic-type-definition 标落地；nested/instance 续作）
- [x] 6.2 `zbc.md` Minor changelog 1.18 + `zpkg.md` 0.20 + `ir.md` Typeof 指令 + 16 冷变体
- [x] 6.3 `roadmap.md` Deferred Backlog Index 更新
- [x] 6.4 ACTIVE.md 释放 compiler+runtime+stdlib 锁 + 归档
- [x] 6.5 z42c writer 同步 follow-up：z42c.ir ZbcFormat.z42 需加 1.18 + Typeof opcode writer。`xtask test compiler-z42` byte-identical gate 暂红（z42c 锁被 port-z42c-self-compile 占），沿用 array-element-type / get-interfaces 先例延后。

## 备注
- z42c writer 同步延后（z42c 锁被 port-z42c-self-compile 占）；`xtask test compiler-z42`
  byte-identical gate 暂红，沿用 array-element-type / get-interfaces 先例。
- 嵌套泛型 type-args（`Box<Map<K,V>>`）超出 MVP，Deferred。
- `new Box<int>()` 实例 `obj.GetType().GetGenericArguments()`（ScriptObject.type_args 路径）
  不在本 MVP，Deferred。
