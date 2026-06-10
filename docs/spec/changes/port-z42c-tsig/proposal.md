# Proposal: port-z42c-tsig — TSIG/IMPL 段 → `z42c build` 全文件 byte-identical

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（zpkg-build 归档后接力）

## Why

`z42c build` 已产出可直跑的 packed zpkg，且 META/NSPC/EXPT/DEPS 对真 C# CLI 逐字节相等；剩余差异**全部**源于 TSIG/IMPL 缺位（真 CLI 恒发导出类型签名段；其串先入 STRS 导致 SIGS/MODS 索引移位）。补上即达成 zpkg 全文件 byte-identical——B 主线对账从 `.zbc` 升到 `.zpkg` 包级。

## What Changes

- **TS-1 模型**：`ExportedTypes.z42`（镜像 C# ExportedModule/ClassDef/MethodDef/FieldDef/ParamDef/FunctionDef 的 z42c 可编译子集；Interfaces/Enums/Delegates/Impls/TypeParams 留空位写 0——z42c 前端尚无这些构造）
- **TS-2 提取器**：`ExportedTypeExtractor.z42`——按 **CU 声明序**（StrMap 不可迭代，确定性铁律）从 AST+SymbolTable 提取类（名/基类/flags/字段[名/类型/可见性/static]/方法[名/ret/可见性/flags/params]）与自由函数（名/ret/minArg/params）；FQ 限定与 visibility 串以 C# 同源产物字节实测为准
- **TS-3 写入**：ZpkgWriterZ + TSIG/IMPL 两段（9 段；intern 时机 = InternZpkgStrings 的 deps 之后、逐模块之前，1:1 C#）；IMPL 写空表（u16 modCount + per-mod ns + 0 impls）
- **TS-4 接线**：driver build 每文件提取 ExportedModule 挂入 ZpkgFileZ
- **TS-5 验证**：xtask e2e 升级——同工程 z42c vs C# CLI（--strip-symbols=false）**全文件逐字节 diff**（buildapp + demo.minimal 两工程）；单测 ×2（extractor 声明序 / TSIG 空类布局）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.ir/src/ExportedTypes.z42` | NEW | TS-1 模型 |
| `src/z42c/z42c.semantics/src/ExportedTypeExtractor.z42` | NEW | TS-2 提取器（CU 声明序）|
| `src/z42c/z42c.semantics/src/IrDump.z42` | MODIFY | BuildModule 旁增 Extract 入口（或合并返回）|
| `src/z42c/z42c.project/src/PackageTypes.z42` | MODIFY | ZpkgFileZ + ExportedModule 数组 |
| `src/z42c/z42c.project/src/ZpkgWriter.z42` | MODIFY | TS-3 TSIG/IMPL + intern 时机 |
| `src/z42c/z42c.driver/src/Main.z42` | MODIFY | TS-4 接线 |
| `src/z42c/z42c.project/tests/zpkg/zpkg_tests.z42` | MODIFY | TSIG 单测 |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | extractor 单测（如放 semantics 侧更顺）|
| `scripts/xtask_compiler_z42.z42` | MODIFY | e2e 全文件 byte-compare |
| `src/z42c/z42c.project/README.md` / `z42c.ir/README.md` | MODIFY | 同步 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 对账状态升级 |

**只读引用**：C# `ZpkgWriter.Tsig.cs` / `ExportedTypes.cs` / `ExportedTypeExtractor.cs` / `ZpkgWriter.Sections.cs`(InternTsigStrings)；`/tmp` 同源产物字节。

## Out of Scope
- 接口/枚举/委托/impl 的**非空**导出（z42c 前端没有这些构造；段内写 0 计数即字节正确）
- 泛型类导出的 TypeParams/约束（z42c 可编译子集暂无泛型类定义导出需求；写 0）
- ZpkgReader / 跨包 import 消费侧（独立大 change：DepIndex/ImportedSymbolLoader）

## Open Questions
- [ ] Q1：visibility 缺省串（C# 侧 "public"/"private" 的精确值）实施首件以同源字节 dump 校准——无需预先裁决
