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

---
**进度（2026-06-23 续2）：z42c golden parity 90/130（PASS）。已修+提交**：typeof(c1cb7055)、嵌套捕获(3552a94c)、复合赋值 +=（216e7292，binder 忽略 op）、表达式体 `=>` 函数/方法（54d81538，parser 整体丢弃）。**剩 40 fail 按根因分类**（src/tests per-cat 快测 /tmp/gscan.sh）：
- **实例字段初始化器**（`int x=1` 未注入 ctor；z42c 仅 static-init）→ class_field_default_init/inherited_fields/static_fields/record 等"got 0/null"。C# 见 FunctionEmitter.cs:76 instanceFieldInits 注入 ctor + 无 ctor 时合成。**高影响，下一个修**。
- **局部函数**（block 内 `int Fact(){}` 未 lift→module fn，被丢）→ local_fn_l2_basic/closure_l3_loops(fe0)/mono(f)。需 parse local-fn stmt + lift。
- **func-typed 值调用→CallIndirect**（`f(x)` where f=Func 参/泛型约束 emit `Call @f` 而非 CallIndirect）→ func_constraint_*(f/handler/pred/Twice)。
- **委托 method-group**（`Demo.a/h` undefined）→ delegate_d1b/nested_delegate。
- **反射 custom-attributes**（"2 vs 0"）→ attributes/* + param_attributes/type_flags/transitive_interfaces（typeof 已通但 attr/flags 元数据 emit 缺）。
- **prefix ++/--**（prefix_increment "1 vs 0"）。**default(T)/默认值**（default_primitives true vs False）。**operator overload**（Vec2 + 运算符未派发）。**enum/null-conditional `?.`/do-while**（Main 体含这些特性→emit 失败→"Main not found"：control_flow/null_conditional·enum·do_while）。**indexer**（get_Item/set_Item ArrayGet on object）。**switch expr**（Monday vs null）。
- **malformed zbc**（generic_bare_typeparam/default_generic_param_pair "cannot parse zbc"）。
- 方法论：每轮 `/tmp/gscan.sh`（robust，file-based）→ 选根因 → 改 z42c → bootstrap-no-csharp（fixpoint）→ 复测 → commit。

---
**进度（2026-06-23 续3）：z42c golden parity 96/130（PASS）**。新增提交：复合赋值(216e7292)、表达式体=>(54d81538)、prefix++(b113d63b)、字段init-ctor注入(8485f715)、func-value→CallIndirect(19891ff1)。注：/tmp/gscan.sh entry 用硬编码 "Main"，对 lowercase `void main()` 或自定义 entry 误报 "Main not found"（如 null_coalesce 实际 PASS）→ 真实失败 ~33（非 34）。
**剩 ~33 fail 按根因**（高→低 ROI）：
- **reflection 元数据 emit**（~8，可能共根）：attributes×3（custom-attr count 2→0）+ type predicates "true got False"×5（interface_class_predicates/type_flags/transitive_interfaces/array_is_instance/param_attributes）。typeof 句柄已通但 TYPE 段 flags/interfaces/attributes 元数据 z42c 未填。**查 TYPE 段 emit**。
- **局部函数 lifting**（~4）：Twice/Fact/fe0/f undefined（block 内 `int F(){}` 未 lift→module fn）。parser 无 local-fn stmt 路径（_parseVarDecl 不处理 `(`）。
- **ctor-less 字段 init 合成**（~3）：class A{int x=1} 无 ctor → x=0。需合成 Class.Class ctor 跑 init（ctor-exists 已修）。
- **null receiver**（~4）：indexer_basic（get_Item/ArrayGet on object）、gc_handle、chained_property、namespace_qualified_free_call、extern_impl（FieldGet/VCall on Null）。
- **运行期值错**（各 1，独立）：do_while、null_conditional `?.`、enum、switch_statement、default_primitives、operator_overload、multicast、closure casts×2（closure_l3_capture/lambda_l2_basic Null→tag）、record。
- **malformed zbc**（~2）：generic_bare_typeparam/default_generic_param_pair。
