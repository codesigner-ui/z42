# Proposal: port-z42c-package-symbols — 同包跨文件符号 + arr.Length（自举缺口 G1/G2）

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（closures 归档后接力）

## Why

自举缺口盘点（探针逐文件喂 z42c.core 真源）实证：四文件 0 错通过，全部失败源于两缺口——
**G1 跨文件符号**：z42c driver 逐文件独立 SymbolCollector，`Diagnostic.z42` 引不到 `DiagnosticSeverity.z42` 的类（C# 经 sibling-exports/<intra> 包机制）；这是"z42c 编译自身"的第一架构闸门。
**G2 arr.Length**：`this._items.Length` 报 "member access on non-class"（数组成员，CG-1D 挂账）——z42c 源 typed array+count 模式的高频写法。

## What Changes

- **PS-1 包级符号收集**（driver/semantics）：build 路径先全文件 parse → **全包 CU 一次性合并收集**进单 SymbolTable（含 imported merge），再逐文件 typecheck/codegen（每文件仍产独立 IrModule——模块切分不变）。语义等价 C# sibling-exports（其逐文件+缓存导出形态为增量编译服务；byte-identity 以同包多文件 corpus 双构建校准）
- **PS-2 arr.Length**：typecheck `Z42ArrayType` 成员 `Length` → int；codegen `ArrayLenInstr`（C# 已有 op——查表）+ 编码 + REGT visit
- **PS-3 验证**：multifile corpus 第 6 zpkg 工程（两文件互引类 + arr.Length + oracle）→ 直跑 + **byte-compare 6/6**；z42c.core 自编译冒烟（7 文件 0 错产 zpkg——自举首包！）；单测 ≥2

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.semantics/src/IrDump.z42` | MODIFY | 包级入口（多 CU 合并收集 + 逐文件产物）|
| `src/z42c/z42c.semantics/src/SymbolCollector.z42` | MODIFY | CollectAll(cus) 合并收集 |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | （如需）Z42ArrayType 成员 |
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | arr.Length → ArrayLen |
| `src/z42c/z42c.driver/src/Main.z42` | MODIFY | build 两趟（收集→编译）|
| `src/z42c/z42c.ir/src/IrInstr.z42` | MODIFY | ArrayLenInstr |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` | MODIFY | op |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcInstr.z42` | MODIFY | 编码 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | REGT visit |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | 单测 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | multifile 第 6 工程 + core 自编译冒烟 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 自举首包里程碑 |

**只读引用**：C# PackageCompiler.BuildTarget（sibling-exports 机制对照）/ZbcWriter ArrayLen 编码；探针缺口清单。

## Out of Scope
- z42c.core 之外的包自编译（syntax 等会暴露下一轮缺口——下个盘点周期）、增量编译、`src/*.z42` 单层子目录 glob 修复（include 用 `**/*.z42` 规避，挂账）

## Open Questions
- [ ] Q1：C# 同包跨文件的 FQ/字节细节（intra 类 ObjNew 名、TSIG 模块序）以 multifile corpus 双构建校准
