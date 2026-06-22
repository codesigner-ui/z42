---
name: project_z42c_impl_block_byte_identical
description: z42c impl-block IMPL 段已实现且结构 byte-identical；full byte-identical 缺口=imported 接口 re-export；正交阻断=exe-with-deps prelude 失活
metadata: 
  node_type: memory
  type: project
  originSessionId: 305f7c33-be53-423e-a53e-b3fc14c715c6
---

z42c `impl Trait for Type`（port-z42c-impl-block）进度（2026-06-23，commit 37904a9d）：

**🟢 里程碑（2026-06-23）**：z42c 端到端建+跑**两个** cross-zpkg fixture 全通过 → impl_propagation="hi from R2"、vcall_base_fallback="Rex says: Woof!/breathes/Dog(Rex)"×2。这使 cross-zpkg→z42c（replace-csharp B）的前置满足（仅 2 个 fixture）。关键修复：impl merge（commit b11ecdef 放宽 trait guard）+ fixture 补 `using Std.IO`（commit 5fe75e44，strict-using 合规——Console 在 z42.io 非 prelude）。C# cross-zpkg 测试仍 2/2 绿（无回归）。**注**：早先"interface-read 破坏 stdlib byte-identical"结论有误——那 6-pkg 差异（StringBuilder.Append）系陈旧本地 C# 参考（Jun22），CI compiler-z42-stdlib 实际绿；interface-read 对 stdlib 无影响（build2/build3 同差异）。byte-identical re-export（step4）非 cross-zpkg 关键路径（输出比对即可），保留为可选。

**已完成（commit 37904a9d）**：IMPL 段 5 组件 A-E 全实现（数据模型 ExportedImplZ + 提取 _extractImpls + intern + emit _buildImpl + read _readImpl + 消费端 _mergeImpl Phase3）。**emit 结构 byte-identical 已验证**：z42c-built greeter IMPL 段 35B，布局/字段编码逐字节 == C#，仅 string-pool 索引值因上游串池漂移而异。C#-free fixpoint gen1==gen2 通过；非-impl 包仍 emit 空 IMPL（implCount=0/模块）→ stdlib byte-identical gate 不变。

**缺口 1（full byte-identical 未达成，根因已定位）**：C# `ExtractInterfaces`（ExportedTypeExtractor.cs）emit **全部** `sem.Interfaces`，含 impl 引入的 imported trait `IGreet` → greeter TSIG 携带 re-export 的本地接口 `IGreet`(方法 Hello)。z42c 只 emit 内建 11 接口 + 本地声明接口，且 imported 接口经 ImportedSymbolLoader Phase1 仅作**无方法骨架**(`new Z42InterfaceType(name)`)载入 → 无法 re-export。实测差：STRS 1245 vs 1342(缺 97B)、缺 bare `IGreet`、TSIG ifaceCount 11 vs 12。**修复**：z42c 须 (a) 载入 imported 接口方法(扩 _fillClass 类比逻辑到接口)，(b) 提取期把 impl trait 当本地接口 emit(去重内建/本地)。注意风险：若 stdlib 包也跨 import 接口，开 re-export 可能动 stdlib byte-identical(当前 gate 绿说明 stdlib 不跨-import 接口，但需复验)。

**缺口 2（正交，`undefined: Console`，精确定位但根因待最终确认）**：`Console` 在 **z42.io 包**(namespace `Std`+`Std.IO`)，**非** prelude `z42.core`(z42.core 只 PreludePackages.Names 一员；z42.core.zpkg 无 Console/无 Std.IO ns)。fixture Main.z42 用 `Console.WriteLine` 却**无** `using Std.IO`。z42c 编译报 undefined(seed 同样复现，与 dep/exe-lib 无关)。加 `using Std;` 即通过(因 z42.io 也有 Std-ns 模块→_pkgProvidesUsing 激活 z42.io→Console 同包载入)。C# 端：LoadExternalImported 激活集 = prelude ∪ using-ns 提供包 = {z42.core,demo.target,demo.greeter}，**z42.io 不在内**，但 committed demo.app.zpkg 却含 `Std.IO.Console.WriteLine` → **强烈怀疑 committed fixture zpkg 陈旧**(早于 strict-using-resolution 2026-04-28，那时 z42.io 可能并入 z42.core 或无 strict 过滤)。**待办**：用当前 C# 重建 fixture 确认——若当前 C# 也报错→fixture 缺 `using Std.IO`(改 fixture，BOTH 编译器都需)；若当前 C# 仍通过→C# 有未找到的 stdlib auto-resolve 机制需复刻到 z42c。consumer merge(Hello)端到端验证被此阻断；probe2(无 Console,return r.Hello())仍报 `no method Hello`→ merge 待缺口1接口 re-export 落地后复验。
（z42vm 解析 entry zpkg 依赖从 **entry zpkg 所在目录**找，非仅 Z42_LIBS → 验证时 z42c.* 放 driver 同目录、Z42_LIBS 只放 stdlib+fixture deps 即避免单-libs 污染。）

验证脚手架：`/tmp/validate-impl.sh`（建 target/ext/main + byte-compare + run）；fixture=src/tests/cross-zpkg/impl_propagation。string-pool diff 工具见会话(python3 解析 STRS/TSIG IMPL 段)。
