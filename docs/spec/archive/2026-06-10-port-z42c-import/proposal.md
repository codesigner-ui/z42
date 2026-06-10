# Proposal: port-z42c-import — 跨包 import 消费侧（z42c 编译 stdlib-using 代码）

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（tsig 归档后接力）

## Why

z42c 至今只能编译自包含程序——源里一句 `using Std;` + `Console.WriteLine` 就过不了 typecheck（Unknown 吸收）也出不了正确字节（无 FQ 调用目标/IMPT/DEPS）。跨包 import 消费侧是"z42c 编译 stdlib、最终编译自身"的钥匙，C# 侧 ~2.9K 行（ZpkgReader 913 + ImportedSymbolLoader 880 + DependencyIndex 198 + pipeline 接线），是 semantics 流剩余最大单件。

## What Changes（MVP 子集：静态类方法调用 + 自由函数；实例方法/接口/委托/枚举/impl 延后）

- **CI-1 ZpkgReader 子集**（z42c.project）：读 zpkg → META(name/kind)/NSPC/SIGS 条目(名/арity/ret/static)/TSIG(→ 复用 ExportedModuleZ 模型)。**不解码 FUNC 体**（DepIndex 只需签名）
- **CI-2 DependencyIndex**（z42c.ir，镜像 C# 位置）：static `"Cls.Method"`→DepCallEntry(FQ 名/ns/арity)；instance `"Method$arity"`；first-wins + 歧义键剔除；**BuildDepIndex**（z42c.pipeline）：libs 目录扫描 **prelude-first Ordinal 排序**（common-pitfalls §1 + fix-depindex 同款）
- **CI-3 ImportedSymbolLoader 子集**（z42c.semantics）：Phase1 骨架 + Phase2 成员填充（类[实例+静态方法+字段] + 自由函数；TypeResolver 优先级子集：数组后缀→prim→imported class→fallback）；按 cu.Usings∪prelude 过滤激活包；ImportedSymbols 持 Classes/Functions/ClassNamespaces
- **CI-4 接线**：SymbolCollector/TypeChecker 合并 imported 进 SymbolTable；ExprEmitter 静态调用查 DepIndex → `CallInstr(QualifiedName)` + TrackDepNamespace（imported 命中先于本地 Qualify 路径）；**DEPS 段真实条目**（usings∪usedDepNs → nsMap[ns→zpkg basename，prelude-first 扫描] → ZpkgDepZ）；driver build 组装（Z42_LIBS 单目录扫描）
- **CI-5 验证**：e2e hello-stdlib 工程（`using Std; Console.WriteLine("hi")`）→ z42c build → ① z42vm 直跑输出 hi；② **vs C# CLI 全文件 byte-compare**（gate 第 3 个工程）；单测 ≥4（reader 自产 zpkg 往返 / DepIndex 键+歧义 / loader 骨架+填充 / DEPS map）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.project/src/ZpkgReader.z42` | NEW | CI-1 读取器子集 |
| `src/z42c/z42c.ir/src/DependencyIndex.z42` | NEW | CI-2 索引（镜像 C# z42.IR 位置）|
| `src/z42c/z42c.pipeline/src/DepScan.z42` | NEW | CI-2 BuildDepIndex + nsMap 扫描（镜像 C# Pipeline.Helpers）|
| `src/z42c/z42c.pipeline/z42c.pipeline.z42.toml` | MODIFY | + z42c.project/ir 依赖（镜像 C# 边）|
| `src/z42c/z42c.semantics/src/ImportedSymbolLoader.z42` | NEW | CI-3 Phase1/2 + TypeResolver 子集 |
| `src/z42c/z42c.semantics/src/SymbolCollector.z42` | MODIFY | imported 合并入口 |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | （如需）imported 解析路径 |
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | CI-4 静态调用 DepIndex 命中 + TrackDepNamespace |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | UsedDepNamespaces 累积 |
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | DepIndex 透传 + usedDepNs 导出 |
| `src/z42c/z42c.semantics/src/IrDump.z42` | MODIFY | BuildModule 带 depIndex/imported 入口 |
| `src/z42c/z42c.project/src/PackageTypes.z42` | MODIFY | ZbcFileZ.UsedDepNamespaces |
| `src/z42c/z42c.project/src/ZpkgBuilder.z42` | MODIFY | DEPS map 组装 |
| `src/z42c/z42c.driver/src/Main.z42` | MODIFY | build：libs 扫描→DepIndex/Imported→编译穿线 |
| `src/z42c/z42c.driver/z42c.driver.z42.toml` | MODIFY | + z42c.pipeline 依赖 |
| `src/z42c/z42c.project/tests/zpkg/zpkg_tests.z42` | MODIFY | reader 往返单测 |
| `src/z42c/z42c.ir/tests/`（depindex 单元新建）| NEW | DepIndex 键/歧义单测 |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | imported typecheck 单测 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | e2e hello-stdlib（直跑 + byte-compare 第 3 工程）|
| `src/z42c/*/README.md`（project/ir/semantics/pipeline）| MODIFY | 同步 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 对账/能力状态 |

**只读引用**：C# `ZpkgReader*.cs`/`ImportedSymbolLoader*.cs`/`DependencyIndex.cs`/`PackageCompiler.Helpers.cs`(BuildDepIndex/ExtractUsings)/`PackageCompiler.BuildTarget.cs`(BuildDependencyMap/ScanLibsForNamespaces)/`FunctionEmitterCalls.cs`(EmitStaticBoundCall)。

## Out of Scope
- **实例方法 DepIndex 命中**（`s.Substring(...)` 等 receiver-aware 防护链）、Console 变参拼接糖、命名参数/默认参数重排、impl 合并（Phase3）、接口/委托/枚举 import、泛型 imported 类实例化、E0601 冲突诊断、indexed zpkg 读取、增量 cached exports

## Open Questions
- [ ] Q1：bare `WriteLine` 键命中哪个重载由 C# 字节实测校准（first-wins 注册序）——无需预先裁决
