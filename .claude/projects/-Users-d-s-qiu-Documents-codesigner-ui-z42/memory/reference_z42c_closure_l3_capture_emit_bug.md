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

---
**扩展（2026-06-23 续）：z42c golden parity 全景**（删 C# 硬前置）。已修：typeof 反射 emit（c1cb7055）+ 嵌套闭包捕获编译崩溃（3552a94c）。剩余 z42c golden 编译缺口（实测 closures 类 1/6 过：仅 closure_l3_stack）：
- **block-body 局部函数**：`int Compute(int x){ var y=x*2; ... }` → `undefined: x`（param 未入 scope）→ 该 fn 不注册 → `undefined function: Compute`。expr-body 局部 fn（`=>`）似可 emit。注：z42c parser 未见显式 local-fn 解析路径（grep 无 LocalFn/local fn），需深查 _parseVarDecl(572，无 `(` 分支)外的处理 / 或 binder lift。
- **higher-order 自由函数 emit**：单文件 emit-zbc `Apply((int x)=>...,5)` → `undefined function Demo.Apply`（自由函数 Apply 没进 module；dump-ir 只见 Main+lambda）。可能 BuildModuleD/单文件路径漏发兄弟自由函数 OR func-type 参数函数被跳。
- **闭包运行期强转**：closure_l3_capture/lambda_l2_basic → `InvalidCastException: Null→tag 0x04`（运行期，编译过）。
- 全量 scan 三次因 zsh process-subst 在后台卡死失败；用 per-cat 快测代替。
- **结论**：golden parity = 多特性 z42c 工作（local-fn/闭包 emit 完整性 + 强转 + 可能更多类别），多轮；在此之前 C# 不可删。删 C# 后续：VM-golden 切 z42c + package/bench/cli 脱 dotnet + 删 src/compiler + CI(~15 setup-dotnet)+ 版本配置。
