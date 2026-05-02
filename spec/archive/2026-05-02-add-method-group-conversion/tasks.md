# Tasks: D1b — 方法组转换 + I12 调用站点缓存

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：lang/ir/vm（完整流程）
> **依赖**：D1a 已 GREEN（delegate 关键字 + 命名 delegate 类型）
>
> **实施备注**：
> 1. zbc `FRCS` section 单 u32 持 module-level slot count；absent → 0。
> 2. `merge_modules` 按 cumulative slot offset 重映射 LoadFnCached.slot_id（与 ConstStr.idx 同款机制）。
> 3. zpkg packed 模式暂不携带 per-module FRCS（slot count = 0）—— stdlib 当前
>    无 method group conversion site 触发，命中前不阻塞；命中后 OOB 会 bail。
>    follow-up：把 FRCS 加到 MODS 段每模块。
> 4. GC root scanner 把 `VmContext.func_ref_slots` 中所有 Value 加入扫描范围
>    （current FuncRef 不持 GcRef，但保留以适配未来 String 池化）。

## 进度概览
- [x] 阶段 1: IR 指令 + zbc 编码 / 解码
- [x] 阶段 2: VmContext + Module loader cache slot 预分配
- [x] 阶段 3: VM interp + JIT 实现 LoadFnCached
- [x] 阶段 4: Codegen 替换 LoadFn → LoadFnCached（仅顶层 / 静态方法路径）
- [x] 阶段 5: 测试
- [x] 阶段 6: 验证 + 文档同步 + 归档

## 阶段 1: IR + zbc
- [x] 1.1 `IrModule.cs` —— 新增 `LoadFnCachedInstr` record
- [x] 1.2 `IrModule.cs::IrModule` —— 增加 `int FuncRefCacheSlotCount`
- [x] 1.3 `Opcodes.cs` —— `LoadFnCached = 0x58`
- [x] 1.4 `ZbcWriter.Instructions.cs` —— 序列化 LoadFnCached（fn_name pool idx + slot_id u32）
- [x] 1.5 `ZbcWriter` —— Module header 末尾写 `func_ref_cache_slots: u32`
- [x] 1.6 `ZbcReader.Instructions.cs` —— 反序列化 LoadFnCached
- [x] 1.7 `ZbcReader` —— Module header 解析新字段
- [x] 1.8 Rust `metadata/bytecode.rs` —— `Instruction::LoadFnCached` variant + `Module.func_ref_cache_slots: u32`
- [x] 1.9 Rust `metadata/zbc_reader.rs` —— 解码

## 阶段 2: VmContext
- [x] 2.1 `vm_context.rs::VmContext` —— `func_ref_slots: RefCell<Vec<Value>>`
- [x] 2.2 `vm_context.rs` —— `alloc_func_ref_slots(n)` / `func_ref_slot(idx)` / `set_func_ref_slot(idx, v)` 方法
- [x] 2.3 `vm.rs` 或 module loader —— 加载时调 `alloc_func_ref_slots(module.func_ref_cache_slots)`
- [x] 2.4 GC root scanner（如需）—— 当前 FuncRef 不持 GcRef，不需特殊处理；记入 follow-up

## 阶段 3: VM dispatch
- [x] 3.1 `interp/exec_instr.rs::LoadFnCached` —— 检查 slot；Null 时构造 FuncRef 写入；非 Null 复制 slot
- [x] 3.2 `jit/helpers_closure.rs::jit_load_fn_cached` —— 镜像 interp 行为
- [x] 3.3 `jit/translate.rs` —— declare_helpers 加 jit_load_fn_cached；MkClos 之后 / LoadFn 旁加 LoadFnCached match arm
- [x] 3.4 `jit/mod.rs::compile_module` —— 注册 `jit_load_fn_cached` 符号

## 阶段 4: Codegen
- [x] 4.1 `IrGen.cs` —— `_funcRefSlots: Dictionary<string, int>` + `_nextFuncRefSlotId`
- [x] 4.2 `IrGen.cs` —— 实现 `internal int GetOrAllocFuncRefSlot(string fqName)`
- [x] 4.3 `IEmitterContext.cs` —— 暴露 `int GetOrAllocFuncRefSlot(string fqName)`
- [x] 4.4 `FunctionEmitterExprs.cs::BoundIdent case` —— TopLevelFn / StaticMethod 路径改 emit `LoadFnCachedInstr`
- [x] 4.5 `IrGen.cs::Generate` —— 最后把 `_funcRefSlots.Count` 写入 IrModule.FuncRefCacheSlotCount
- [x] 4.6 `EmitLambdaLiteral` —— 保留 LoadFnInstr（不缓存 lambda lifted name）

## 阶段 5: 测试
- [x] 5.1 NEW `src/compiler/z42.Tests/MethodGroupConversionTests.cs` —— 6 个 IR-dump 测试
- [x] 5.2 NEW `src/runtime/tests/golden/run/delegate_d1b_method_group/source.z42`
- [x] 5.3 NEW `src/runtime/tests/golden/run/delegate_d1b_method_group/expected_output.txt`
- [x] 5.4 `./scripts/regen-golden-tests.sh`（全部 zbc regen 因 module header 变化）

## 阶段 6: 验证 + 文档 + 归档
- [x] 6.1 `dotnet build` / `cargo build` 双绿
- [x] 6.2 `dotnet test` 全绿（基线 +6）
- [x] 6.3 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 6.4 spec scenarios 逐条对应实现位置
- [x] 6.5 文档同步：
    - `docs/design/closure.md` §6.4.1 mono 末尾加 "+ D1b 把 LoadFn 升级为 LoadFnCached（I12）"
    - `docs/design/delegates-events.md` D1b 完成标记
    - `docs/design/ir.md` —— 新增 `LoadFnCached` opcode 描述
    - `docs/design/vm-architecture.md` —— `VmContext.func_ref_slots` 简介
    - `docs/roadmap.md` —— 加一行
- [x] 6.6 移动 `spec/changes/add-method-group-conversion/` → `spec/archive/2026-05-02-add-method-group-conversion/`
- [x] 6.7 commit + push

## 备注
- slot 全模块去重 + Vec 索引（design Decision 1/3）
- alias direct-call 路径不动（mono spec 已优化为零 emit）
- lambda lifted name 不缓存（design Decision 6）
