# Proposal: port-z42c-interface — interface 类型系统 + 分派

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（try 归档后接力）

## Why

三大件第②。z42c typecheck 无 Z42InterfaceType：interface 声明被 SymbolCollector 跳过、`class Box : IShape` 的 IShape 被误当基类（IrClassDesc/TSIG base 字节错）、接口类型变量/参数/分派全不可用。探针实证 C# 形态：接口**不产 TYPE 条目**、分派 = VCall 短名（codegen 零新指令）、元数据只走 TSIG——主体是 semantics 工作。

## What Changes

- **IF-1 类型系统**：`Z42InterfaceType`（方法签名表）+ SymbolTable.Interfaces + SymbolCollector 收集 interface 声明 + **基表判别**（`class Box : Base, IShape`：解析出接口→入 Z42ClassType.InterfaceNames，类→base；探针实证 base 仍 Std.Object）
- **IF-2 typecheck**：ResolveType 接口名；可赋性 class→interface（类+base 链的接口表查找）；接口收者成员调用（接口方法签名→instance BoundCall）；is/as 接口；ToIrType(interface)→Ref
- **IF-3 IrGen/TSIG**：IrGen._classDesc 基表判别（接口剔除，base 回落 Std.Object）；TSIG：ExportedClassZ.Interfaces 字段（writer 类条目接口表替换恒 0）+ 本地 interface 声明导出（ExportedInterfaceZ，方法 flags=virtual|abstract）+ extractor 同步
- **IF-4 验证**：ifacecheck 第 6 zbc 源（接口声明/实现/接口参数分派 + oracle，C# 探针已通）→ 执行 + **byte-compare 6/6**；单测 ≥3（收集/可赋性/分派绑定）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.semantics/src/Z42Type.z42` | MODIFY | Z42InterfaceType |
| `src/z42c/z42c.semantics/src/SymbolTable.z42` | MODIFY | Interfaces 表 + 解析 |
| `src/z42c/z42c.semantics/src/SymbolCollector.z42` | MODIFY | 收集 + 基表判别 |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | 可赋性/接口调用/is·as |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | ToIrType 接口→Ref |
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | _classDesc 基表判别 |
| `src/z42c/z42c.semantics/src/ExportedTypeExtractor.z42` | MODIFY | 本地接口导出 + 类接口表 |
| `src/z42c/z42c.ir/src/ExportedTypes.z42` | MODIFY | ExportedClassZ.Interfaces |
| `src/z42c/z42c.project/src/ZpkgWriter.z42` | MODIFY | TSIG 类条目接口表 |
| `src/z42c/z42c.project/src/ZpkgReader.z42` | MODIFY | 类条目接口表读取（原跳过） |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | 单测 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | ifacecheck 第 6 源 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 同步 |

**只读引用**：C# `Z42Type.cs`（Z42InterfaceType）/`SymbolCollector.Classes.cs`（接口收集+判别）/`TypeChecker` 可赋性段；/tmp/iface_cs.zbc 探针字节。

## Out of Scope
- 泛型接口（IComparable<T> 实例化）、static abstract 接口成员、impl 块、接口继承接口、E0xxx 实现完备性诊断（缺成员检查——按 C# 行为校准后若有再补）

## Open Questions
- [ ] Q1：class 缺接口成员时 C# 是否报错（实现完备性检查）——探针校准后决定本期是否镜像
