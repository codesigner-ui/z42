---
name: reference_z42c_closure_l3_capture_emit_bug
description: "z42c ZbcWriter 编 closure_l3_capture（嵌套/ref/local-fn 捕获组合）Null deref；C#-free golden regen 暂跳，待修"
metadata: 
  node_type: memory
  type: reference
  originSessionId: 305f7c33-be53-423e-a53e-b3fc14c715c6
---

**症状**：`z42vm z42c.driver.zpkg -- --emit-zbc src/tests/closures/closure_l3_capture.z42 out.zbc` 抛
`Std...: FieldGet: not an object or known value type, got Null`，栈：`ZbcInstr._args` → `ZbcInstr.WriteInstr`
→ `ZbcWriter.BuildFunc` → `ZbcWriter.Write` → `IrDump.ZbcBytes`。即 **z42c 自身代码** 在编该 golden 时
对某 IR 指令操作数做 Null 字段访问（z42c 的 NRE 等价）。C# 编译器能编此 golden，z42c 不能 → z42c 闭包
codegen/emit 的缺口（具体在 closure env 捕获产生的指令某操作数为 null，ZbcInstr._args 迭代 args 取 .Id/.Type 时 deref null）。

**触发特征**：closure_l3_capture 组合了值快照捕获 + ref 捕获(`c.n` mutation) + higher-order 传递 + **嵌套捕获**
（inner lambda 经 outer env 间接捕 k2）+ **local-fn 捕获**（`int Helper(int x)=>x+prefix`）。closcheck fixture
（self-hosting 已过 zpkg 5/5）未覆盖这种组合 → 此 golden 才暴露。

**现状（2026-06-23）**：replace-csharp Phase C 把 golden regen 改 z42c（C#-free）后此 golden 阻断。**tracked
interim**：`scripts/xtask_regen.z42` 跳过 `closure_l3_capture`（删旧 .zbc 防 stale；VM golden 测枚举产物
.zbc，未产即不跑）。199 goldens → 198 经 z42c regen+跑，1 跳。

**修复方向**（dogfood：应真修非长期跳）：debug z42c 闭包 IrGen（z42c.semantics）——找出 closure env
捕获（MkClos/CallIndirect/FieldGet on env）哪条指令的 Dst/Obj/arg 寄存器为 null 未填，补上。修后移除
xtask_regen.z42 的跳过 + 复跑 `xtask test`（vm goldens 应含 closure_l3_capture）。属 z42c 子系统独立 change。
