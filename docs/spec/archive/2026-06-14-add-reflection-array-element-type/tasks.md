# Tasks: 数组元素类型反射（运行期不擦除）

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-14 | 类型：ir（格式 bump + 核心 VM，完整流程）

## 阶段 0
- [x] 0.1 6.5 gate 审批（User 已选方案 A：统一 Type + 不擦除）

## 阶段 1: 二进制格式（ArrayNew/Lit 元素类型字段）
- [x] 1.1 `bytecode.rs`：`ArrayNew`/`ArrayNewLit` 装箱为 `Box<XxxInsn>` + `element_type: String`（`#[serde(default)]`，保 slim invariant）
- [x] 1.2 `zbc_reader.rs`：读 ArrayNew/Lit element_type；`ZBC_VERSION_MINOR`=16 / `ZPKG_VERSION_MINOR`=18 + version-pin 测试 + 注释 changelog
- [x] 1.3 C# `ZbcWriter.cs` `VersionMinor`=16 + ArrayNew/Lit 写 element_type idx；`ZbcReader.Instructions.cs` 读；`ZpkgWriter.cs` `VersionMinor`=18
- [x] 1.4 C# IR：`IrModule` ArrayNewInstr/ArrayNewLitInstr 携带 `ElementTypeName`
- [x] 1.5 `docs/design/runtime/{zbc,zpkg}.md` changelog（ir.md 无 per-opcode 表，wire 细节归 zbc.md）

## 阶段 2: VM 数组表示（interp-first）
- [x] 2.1 `arc_heap.rs`/`types.rs`：`ArrayObj{element_type,elems}` + `Deref/DerefMut/Index/IndexMut`；`alloc_array_typed`；GC trace `.elems`；`StrongArray/WeakArray` payload
- [x] 2.2 `GcRef<Vec<Value>>` → `GcRef<ArrayObj>`（gc/* + metadata/types.rs + soft_registry/types）
- [x] 2.3 `exec_array.rs`：`ArrayNew`/`ArrayNewLit` 取 element_type → alloc_array_typed
- [x] 2.4 `cargo build` + `cargo test --lib` 807/0（数组单测不回归）

## 阶段 3: JIT 同步
- [x] 3.1 `jit/helpers/{array,closure,registry}.rs` + `translate.rs` 适配 `ArrayObj` + 传 element_type
- [x] 3.2 vm goldens jit 模式全绿（173/0）

## 阶段 4: 反射表层
- [x] 4.1 `reflection.rs`：`build_type_ex` 写 `IsArray` + `__elementName` 槽
- [x] 4.2 `make_type_from_name` 认 `[]` 后缀 → array Type；`builtin_type_element`（`__type_element`）
- [x] 4.3 `object.rs`：`arr.GetType()` 读 `ArrayObj.element_type` → `<elem>[]` Type
- [x] 4.4 `corelib/mod.rs`：注册 `__type_element`

## 阶段 5: 编译器 + stdlib
- [x] 5.1 `FunctionEmitterExprs.cs`：`Z42TypeName` 助手；ArrayNew/ArrayNewLit emit 元素类型 FQ 名；VisitTypeof `<elem>[]`
- [x] 5.2 `Type.z42`：`public bool IsArray;` + `public string __elementName;` + `GetElementType()`（extern `__type_element`）

## 阶段 6: z42c 自举 writer 同步 —— ⏸ 延后（z42c 子系统被 port-z42c-statics-arrays 占用）
- [ ] 6.1 `z42c.ir/.../ZbcFormat.z42` 版本 16；`ZbcWriter.z42` ArrayNew/Lit 镜像 element_type；`ZpkgWriter.z42` 18
- [ ] 6.2 `zbc_tests.z42` golden 重截
- 说明：z42c 锁被并行 change 持有；本 change 走 dotnet 权威门 + cargo + xtask vm/cross-zpkg/stdlib 全绿归档。`xtask test compiler-z42` byte-identical gate 暂红，待 z42c 归还锁后由 follow-up 补齐（记入 memory `project_z42c_selfhosting`）。

## 阶段 7: 验证
- [x] 7.1 fixtures regen ×2（zbc 6/6 + zpkg 12/12）+ stdlib regen（zpkg 0.18）+ embedded hello.zbc 1.16
- [x] 7.2 golden `array_element_type.z42`（typeof/字段/字面量/`arr.GetType` 不擦除/空数组/用户类元素/非数组返 null；interp+jit）
- [x] 7.3 dotnet GoldenTests 1561/1561（回归 array_get_type/object_get_type + 全数组 e2e）
- [x] 7.4 cargo test：lib 807/0 + 集成（cross_thread/manifest/native_interop/native_opcode/native_pin/zbc_compat 全绿）；signal_handler_e2e 沙箱预存挂起（崩溃信号捕获，与本变更无关，Windows CI 验证）
- [x] 7.5 docs reflection.md 同步（用法 + 不擦除原理 + Deferred 标记落地）+ spec scenarios 覆盖

## 备注
- 大 change：格式 bump + 核心数组堆表示 + 4 子系统（runtime/compiler/stdlib/z42c）。interp-first，JIT 同步，每阶段 build 卡点。
- 风险已验证：GC trace 正确性（trace `.elems`，796 lib 单测过）、JIT array helper（jit goldens 173/0）、Deref/Index 覆盖所有消费点（编译错逐个补，最终 0 错）。
- slim invariant：inline `element_type: String` 会破 32B `instruction_size_is_slim` → ArrayNew/ArrayNewLit 装箱为 `Variant(Box<XxxInsn>)`，serde 自动摊平保 wire 兼容。
- z42c writer 同步延后（见阶段 6），是本 change 唯一未竟项，已就近留痕 + follow-up 跟踪。
