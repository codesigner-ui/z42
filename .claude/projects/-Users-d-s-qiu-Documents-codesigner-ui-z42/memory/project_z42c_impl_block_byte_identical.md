---
name: project_z42c_impl_block_byte_identical
description: z42c impl-block IMPL 段已实现且结构 byte-identical；full byte-identical 缺口=imported 接口 re-export；正交阻断=exe-with-deps prelude 失活
metadata: 
  node_type: memory
  type: project
  originSessionId: 305f7c33-be53-423e-a53e-b3fc14c715c6
---

z42c `impl Trait for Type`（port-z42c-impl-block）进度（2026-06-23，commit 37904a9d）：

**已完成（commit 37904a9d）**：IMPL 段 5 组件 A-E 全实现（数据模型 ExportedImplZ + 提取 _extractImpls + intern + emit _buildImpl + read _readImpl + 消费端 _mergeImpl Phase3）。**emit 结构 byte-identical 已验证**：z42c-built greeter IMPL 段 35B，布局/字段编码逐字节 == C#，仅 string-pool 索引值因上游串池漂移而异。C#-free fixpoint gen1==gen2 通过；非-impl 包仍 emit 空 IMPL（implCount=0/模块）→ stdlib byte-identical gate 不变。

**缺口 1（full byte-identical 未达成，根因已定位）**：C# `ExtractInterfaces`（ExportedTypeExtractor.cs）emit **全部** `sem.Interfaces`，含 impl 引入的 imported trait `IGreet` → greeter TSIG 携带 re-export 的本地接口 `IGreet`(方法 Hello)。z42c 只 emit 内建 11 接口 + 本地声明接口，且 imported 接口经 ImportedSymbolLoader Phase1 仅作**无方法骨架**(`new Z42InterfaceType(name)`)载入 → 无法 re-export。实测差：STRS 1245 vs 1342(缺 97B)、缺 bare `IGreet`、TSIG ifaceCount 11 vs 12。**修复**：z42c 须 (a) 载入 imported 接口方法(扩 _fillClass 类比逻辑到接口)，(b) 提取期把 impl trait 当本地接口 emit(去重内建/本地)。注意风险：若 stdlib 包也跨 import 接口，开 re-export 可能动 stdlib byte-identical(当前 gate 绿说明 stdlib 不跨-import 接口，但需复验)。

**缺口 2（正交 pre-existing 阻断，非 impl 引入）**：z42c 编译**含显式 `[dependencies]` 的 exe** 时 prelude(z42.core)未激活 → `undefined: Console`。seed z42c(无本次改动)同样复现 → 确认 pre-existing。声明依赖(Robot)解析正常，仅 prelude 失活。这是 cross-zpkg→z42c(S2.3 / [[project_csharp_to_z42c_replacement]] B)真正阻断项，须独立 change 修(driver Main.z42 exe import-loading 路径 prelude 激活；ImportedSymbolLoader.Load 的 active[] 计算)。consumer merge 端到端验证被此阻断；probe2(无 Console,return r.Hello())仍报 `no method Hello` → merge 待缺口1接口 re-export 落地后复验。

验证脚手架：`/tmp/validate-impl.sh`（建 target/ext/main + byte-compare + run）；fixture=src/tests/cross-zpkg/impl_propagation。string-pool diff 工具见会话(python3 解析 STRS/TSIG IMPL 段)。
