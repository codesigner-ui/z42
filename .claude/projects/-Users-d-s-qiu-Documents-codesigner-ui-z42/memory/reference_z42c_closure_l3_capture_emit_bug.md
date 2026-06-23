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

---
**reflection 子根因定位（2026-06-23 续4）**：z42c ZbcWriter.BuildType（z42c.ir）**确实**发 TYPE 段（name/base/fields/typeparams/flags/static/interfaces）。但：
- **class custom-attr 硬编码 0**：ZbcWriter.z42:174 `WriteU16(0) // attrCount`、field attr 同（149/182）→ attributes/basic·field_attrs·methods + param_attributes "got 0/False"。需 IrClassDesc 收 AttributedDecl 的 attr + 发 attr-ref（zbc 1.10/1.14 格式，镜像 C# BuildType attr 块）。
- **flags/interfaces 已发**（175/185）→ type predicates "true got False"（interface_class_predicates/type_flags/transitive_interfaces/array_is_instance）应查 IrGen 建 IrClassDesc 时 Flags/Interfaces 是否填对（疑 IrClassDesc.Flags=0 / Interfaces 空），非 writer。下一轮查 IrGen IrClassDesc 构建。

---
**进度（2026-06-24）：z42c golden parity 120→127/130**。本会话提交 4 个（均 fixpoint gen1==gen2 7/7 逐字节 + scan 零回归）：
- `a291a613` extern_impl：**CollectWithImports 漏 _passImpls**（deps 路径 impl 方法不入 ct.Methods → _bindImpl ms=null → this 不入 env → this.field 绑 undefined → emit const.null → FieldGet on Null）。
- `6310e075` gc_handle：**跨包导入枚举常量**（ZpkgReader.ReadTsig 写 enum 但读时硬编 `new ExportedEnumZ[0]` 丢弃 + ImportedSymbols/CollectWithImports 不携带/合并 → GCHandleType.Weak 绑 undefined → const.null）。修：ReadTsig 解码 enum + ImportedSymbols.EnumTypeNames/EnumConsts + _mergeImportedEnums。
- `3e40c045` chained_property：**消费端 Object stub**（z42.core Object 不导出 TSIG → 消费端符号表无 Object → obj.GetType() 返回 Unknown → 链式 getter 退化 FieldGet）。修：SymbolCollector CollectWithImports/CollectAll 在 merge imports 后 seed Object 骨架（4 协议方法，GetType 返回真实 Type 类）。镜像 C# SymbolCollector.Classes.cs。
- `51d22ef2` **attribute 反射全 4 级**（basic/methods/field_attrs/param_attributes）：parser 命名实参 `method: "POST"`→AssignExpr（此前 colon 丢整条 attr）+ 参数 attr；AttributeSynth.z42（新，parse 后合成 `__attr$<key>$<i>` 工厂自由函数，跳内建 Native/Test/Benchmark/Skip/ShouldThrow/Timeout）；TypeChecker._adaptArgs（new 命名实参重排+默认值填充——z42c 此前 new 完全不填默认/不重排命名）；IrGen _attrRefs/_paramAttrRefs（FQ 限定）；IR IrAttrRef/IrAttrRefList；ZbcWriter 四级 attr-ref 发射+intern。basic byte-identical to C#。

**诊断法（关键）**：z42c emit 的 ZBC 与 C# oracle（`dotnet z42c.dll <src> --emit zbc` + `disasm`/`golden-json`）逐条对比——VM 是常量，差异全在编译器输出。注意 disasm(.zasm) **不渲染 TYPE/SIGS attr-ref 字节**，须用 raw `cmp` 或 `golden-json`。快迭代：`/tmp/rebuild-z42c.sh`（seed 重建 z42c ~1min，免 5min 全 bootstrap）→ 测 → 最终 bootstrap-no-csharp 验 fixpoint。

**剩 3 fail（均大特性，各≈attribute 量级）**：
- **multicast_func_predicate**：event 关键字字段（minimal MulticastFunc 用法已 PASS，仅 event 字段缺失）。需：parser `event` 入 _isModifier；SymbolCollector 为 event 字段合成 add_X/remove_X 方法符号（add_X body=field.get X; vcall Subscribe；remove_X=Unsubscribe）；synth-ctor 自动初始化 event 字段（obj.new @Std.MulticastFunc/MulticastPredicate + field.set，按字段基类型名）；TypeChecker `+=`/`-=` on event 字段 → vcall add_X/remove_X（rhs lambda 须以 handler func 类型为 expected 绑定——handler 类型须从 Multicast<...> 泛型实参推导）。C# 参考 disasm /tmp/mc_cs.zasm（add_Validate params:2 ret IDisposable；Validators.Validators synth ctor obj.new+field.set）。
- **local_fn_l2_basic**：block 内局部函数 lift（`int Fact(int n)=>...; return Fact(n)`）。C# 设计见 docs/spec/archive/2026-05-01-impl-local-fn-l2/design.md：parser 检测 local-fn decl→LocalFunctionStmt(FunctionDecl)；TypeChecker BindBlock 两趟（pass1 收集 local fn 签名入 scope→支持前向引用/递归，pass2 绑定）；BoundLocalFunction；IrGen lift 为 `<Owner>__<Name>` 顶层 fn + call site 改用 lifted 名。**此前尝试崩溃（VCall null）已 revert**——慎重，从 C# BoundStmtRewriter/FunctionEmitterStmts.cs 忠实移植。
- **closure_l3_capture**：见本文件顶部，最难（ZbcWriter null-deref 嵌套/ref/local-fn 捕获组合），仍 tracked-defer。

**删 dotnet 路径**：130/130 后（或 tracked-skip 这 3，沿用 closure_l3_capture 先例）→ VM-golden gate 切 z42c → 删 src/compiler+z42.Tests → 清 CI ~15 setup-dotnet + 版本配置。User 选 path A（逐个攻到 130 再删）。

---
**进度（2026-06-24 续）：z42c golden parity 127→129/130**。再提交 3 个（均 fixpoint gen1==gen2 7/7 + scan 零回归）：
- `0bbf507a` multicast：event 修饰 + 字段自动 init（合成 `new <type>()` 复用字段 init 注入）+ `+=`/`-=` on event 字段 → recv.E.Subscribe/Unsubscribe 脱糖（Z42ClassType.EventFields 判别）。
- `ba7f013c` local_fn L2 lifting：parser _isLocalFnStart（跳类型后 Ident `(`）+ LocalFunctionStmt + TypeEnv.LocalFns（DefineLocalFn/LookupLocalFn 父链）+ block bind 两趟（pre-pass 收签名→前向引用/递归）+ FunctionEmitter lifted 名 `<Owner>__<name>` 经 **StrBox** 入 EmitContext.LocalFnNames（**关键坑：StrMap string 值必须 StrBox 包装，`Get() as string` 对裸 string 返 null → lifted 名 null → BuildStrs 池含 null 崩溃**；镜像 ImportedClassNs/EnumConsts）+ Gen.AddLifted（同 lambda）+ free 调用改写 lifted 名 + SeedLocalFns 注入 sub-emitter（递归/兄弟解析）。

**剩 1 fail：closure_l3_capture（最难，仍 tracked-defer）**。深查（2026-06-24）：5 个 case，已破解前 4 个所需机制但**无法安全落地**：
- **case 2（ref 捕获 `c.n = c.n+1`）**：lambda 体赋值优先级 bug——`() => c.n = c.n+1` 被解析为 `(()=>c.n) = (c.n+1)`（lambda 体只取 `c.n`，RHS 漏到外层）。Parser.z42:1490 lambda 体 `_parseExpr(11)`（赋值层之上）→ 应含赋值。**但改 11→10 灾难性：scan 73 EMITFAIL**（根因未明，Pratt 分析说只应影响赋值体 lambda 但实测广泛炸；勿轻改）。已 revert。
- **case 5（local-fn 捕获 `Helper(x)=>x+prefix` 捕 prefix）**：L2 lifting 把 Helper 发自由函数 → prefix undefined（Null）。需 L3：捕获分析（复用 _lambdaActive）+ 合成 BoundLambda 复用 EmitLambdaLifted（env 版）+ MkClos + Locals 存闭包（call 经 func-typed Locals → CallIndirect）。已实现但因 case-2 bp 阻断未能整体验证，连同 bp 一起 revert。
- **结论**：closure_l3 需 ① 安全的 lambda 赋值体优先级修（先查清 bp-10 73-炸根因）② case-4 嵌套捕获 ③ case-5 L3 local-fn 捕获，三者齐活方过。是整个 L3 闭包系统的集成测试，工作量大、bp 改动高危。建议：要么继续深挖（多轮），要么 tracked-skip（沿用先例）后推进删 dotnet。
- 复现/诊断：`/tmp/refcap.z42`（case2 最小）、`/tmp/lfmin.z42`（local-fn 最小）；oracle diff 见 case-2 lifted lambda 缺 field.set + RHS 漏到 Main。
