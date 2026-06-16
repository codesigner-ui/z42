# Tasks: port-z42c-self-compile

> 状态：🟡 进行中 | 创建：2026-06-15

**变更说明：** dogfood gap-batch——让自举 z42c 编译器能编译**自己的源码**（src/z42c/
7 包），逐个补齐 z42c 当前缺的语法/语义/stdlib 缺口（feedback_dogfood_fill_gaps：
缺口在 z42c 里实现，不绕过）。从最小叶子包 z42c.core 起，逐包推进。

**方法：** 用自举 driver（`z42c build <member-src-toml>`）编译各 z42c 包源 → 遇错即定位
缺口 → 在 z42c 实现 → 重建重试。最终目标：z42c byte-identical 编译全部 7 包 = 完整自举。

## 进度（按发现顺序）
- [x] G1 `_isVarDeclStart`：识别 `Name[] var`（用户类数组类型局部声明）。**z42c.core 全包自编译通过**（无错）。
- [x] G2 `_bindMember`：prim receiver 属性访问（`s.Length`/`s.ByteLength` 等）镜像 `_bindMemberCall` 的 prim→stdlib 包装类映射；查无松绑 Unknown。z42c.ir/ByteWriter.z42 触发。
- [x] G3 C 风格强制转换 `(Type)expr`（`(int)`/`(byte)`/`(long)` 等类型关键字 cast）。全流程：CastExpr AST + parser cast 消歧（`( 类型关键字 )` 无歧义）+ BoundConvert + ConvertInstr（op 0xB1，镜像 C# ConvertInstr）+ ExprEmitter `_emitConvert`（镜像 C# VisitCast no-op 规则）+ REGT visit。
- [x] G4 整型字面量 radix 解析（`0xFF`/`0b..`/`_` 分隔/尾后缀）：`ZbcInstr._parseIntLit`（统一 `_parseRadix`，镜像 C# ParseIntLit）替代裸 `Convert.ToInt32/64`（仅解十进制）。**z42c.ir 全包自编译通过**。
- [x] G5 基本类型别名等价（`byte[]`≡`u8[]`）：`Z42Type.Canon`（剥 nullable `?` + prim 别名归一）。z42c.project/ZpkgBuilder 触发。
- [x] G6 数组元素 unknown/error 吸收（`IrXxx[]`→`<unknown>[]`，imported 元素类型在字段位退化）：Z42ArrayType.IsAssignableTo 吸收。z42c.semantics/EmitContext 触发。
- [x] G7 nullable 规范化（`string?`→`string`）：Canon 剥 `?` → Prim 可赋性等价。z42c.driver/Main 触发。
- [x] G8 无初始化器局部声明 `Type name;`：parser init 可选 + typecheck 跳赋值检查 + codegen 无 init 不发 IR（变量首赋值绑定，镜像 C# VisitVarDecl）+ Dump null 守卫。z42c.semantics/ExportedTypeExtractor 触发。

## 🎉 里程碑：z42c 自编译全部 7 个自身包（功能性自举）
G1-G8 后 **`z42c build` 编译 z42c.{core,ir,syntax,project,semantics,pipeline,driver} 全部 7/7 无错**。
下一级：逐包 byte-identical 对账（z42c 产物 vs C# CLI；workspace 默认 [sources] + 跨包 namespace/版本上下文对齐）。

- [x] G9 默认 `[sources]`：ManifestLoader 在 `[sources]` 段缺失/无 include 时回落 `["src/**/*.z42"]`（镜像 C# ProjectManifest.ParseSources），exclude 默认 `[]`；SourceDiscovery `_expand` 支持中段 `<prefix>/**/<suffix>`（`src/**/*.z42` → `GlobRecursive(projectDir/src, "*.z42")`，覆盖 z42c.ir/src/BinaryFormat/ 嵌套）。**使 `z42c build <member-toml>` 无需显式临时 toml 即可发现源**，逐包对账前置。gate 6/6+7/7 无回归（合成项目用显式 include 不受影响）。

## 🔬 逐包 byte-identical 首测（z42c.core，最简叶子）
G9 落地后首次对真实包 standalone 双路构建对账（隔离 .cache 防 z42c 写入被 C# 复用污染）：
**z42c=27942B vs C#=29046B，几乎每段分歧**（非单 bug，是真实包暴露的小合成 corpus 未覆盖构造）：
| Section | z42c | C# | 诊断 |
|---|---|---|---|
| EXPT | 124(24 项) | 144(28 项) | **z42c 少导出 4 个符号** |
| DEPS | 14(count=1) | 4(count=0) | **z42c 虚假多加 1 依赖**（str idx 25，疑 Std；z42c.core 无 using/无 deps 应为 0）|
| SIGS | 628 | 770 | +142 |
| MODS | 7599 | 8348 | **+749（最大，函数体/zbc codegen 分歧）** |
| TSIG | 9805 | 9941 | +136 |
| STRS | 9576 | 9643 | 下游（DEPS/EXPT/SIGS 串入池）|
→ 逐包 byte-identical 是 0.3.x B 退出里程碑（multi-bug，建议独立 change：DEPS→EXPT→SIGS/MODS codegen parity 逐个对账，从 z42c.core 起逐包推进）。

## 🔧 z42c.core byte-identical 逐段攻坚（从 DEPS 起）
- [x] G10 DEPS 自依赖（z42c.core 虚增 dep=`z42c.core.zpkg` ns=`Z42.Core`——flat libs 含 self → 同包符号经 DepIndex 解析）：`DepScan.Scan(libsDir, excludeName)` 排除 self-zpkg（包不导入自己；C# libs=stdlib only 本无 self，排除是 no-op）。**DEPS 4=4 对齐**。gate 7/7+6/6 无回归。
- [x] G11 构造器 lowering：SymbolCollector 收集 ctor（名=类名，ret void）+ TypeChecker 绑 ctor 体 + IrGen 发 `<qualClass>.<Class>` 实例函数（ret void）+ ExportedTypeExtractor 入 TSIG + EmitFunction ctor ret void。exports 自动来自 irm.Functions。**EXPT 144=144 ✓ / SIGS 770=770 ✓ / TSIG 9941=9941 ✓ / MODS +749→+16**。codegen 单测 test_ctor_lowering。
- [x] G11b static_init namespace 限定：IrGen `_q(stem).__static_init__`（镜像 C# `<ns>.<stem>.__static_init__`；无 ns 退化 stem，sacheck 不回归）。**STRS 10000=10000 ✓**。
- [x] G12 class-shape flags（typeData 0-vs-2）：mod[0] Span 完全一致。
- [x] G13 形参 REGT/指令 tag 镜像 C# 语法重载 `ToIrType(TypeExpr)=GetIrType(name)`：prim→tag / array→Ref / class·iface·func·泛型→**Unknown**（GetIrType 表只含 prim；体内值才走语义 ToIrType→class=Ref）。FunctionEmitter 形参循环原只特例 interface/Func，扩到所有非-prim·非-array→Unknown。corpus 唯一 class 形参=`IShape`(接口已特例)+`MyErr`(catch 变量另路径)→无回归。**消除 mod[1] Span 形参 Ref-vs-Unknown 差**（首差 211→533）。
- [x] G14 prim→wrapper BCL 名映射（`_primWrapper` 替 `_capFirst`）：`int.ToString()` 的 wrapper 应为 `Int32`（C# WellKnownTypes/TypeRegistry 名）非 `_capFirst("int")="Int"` → 旧版查无 → fallback Unknown → vcall 返回 tag Ref；现 int→Int32/long→Int64/float→Single/bool→Boolean… → ToString 返回解析 string → tag Str。string→String 巧合相同故旧版 string 方法能用；int/long/float 全错。**mod[1] funcData/typeData/regt 全对齐**（跨文件成员解析本就正常，根实为 prim wrapper 名）。corpus 不用 int.ToString() → 无回归。
- [x] G15 DBUG line-tracking（z42c.core 收口 → **9/9 段全 byte-identical**）：C# 在**语句级 + 每 EmitExpr 入口**都 `TrackLine`（LastLine dedup）；z42c 原只 _emitStmt 级。修：`ExprEmitter.Emit` 入口加 `TrackLine(e.Span)`（镜像 C# EmitExpr）。**之前会话误判"过头 +57"的根因实为 `__static_init__`**：C# `EmitStaticInit` 返回的 IrFunction 用**省略 lineTable 的构造重载**，丢弃 EmitExpr 期累积的行（DiagnosticCodes 53 + DiagnosticSeverity 3 = 56 条静态字段 init 行 + Format 多行 return 1 条 = 57）。修：`FunctionEmitter.EmitStaticInit` 同样丢弃行表/局部表（lines=∅, localVars=∅）。**实证**：加 Emit TrackLine 后逐函数 DBUG diff → 只有 2 个 `__static_init__` 函数分歧，全部其他函数（方法/ctor/factory）已与 C# 完全一致 → 证明 Emit≡EmitExpr 调用面本就 1:1（之前"调用面不齐"判断有误，真因是 static_init 特例）。
- [x] G16 ArrayNew 元素类型名 FQ 化（z42c.core cmp 暴露的第 2 个 latent bug，2 字节）：`new Diagnostic[]` 的元素名 z42c 发**短名** `Diagnostic`，C# 发 **FQ** `Z42.Core.Diagnostic`（镜像 add-reflection-array-element-type：数组非擦除，elem 名须可解析）。修：`ExprEmitter` BoundArrayNew 分支按元素 Z42Type 类别——prim/泛型形参保留短名、类·实例化经 `QualifyClass`（同 C# Z42TypeName 派发）。**🔴 关键认知：zpkgsec.py 段尺寸对账对 same-size 内容差盲**（短名/FQ 名都在 STRS 池里、FUNC 引用 u32 idx 尺寸相同）→ 此 bug 一直被"8/9 段尺寸一致"假象掩盖，**cmp 逐字节才暴露**。逐包对账今后一律 `cmp` 不只比段尺寸。顺带：`ArrayNewInstr.Dump` 补 ElemName（原缺失 → IR dump 看不出元素类型）。codegen 单测 `test_array_new_user_class_fq`（FQ）/ `test_array_new_prim_short`（短名）；默认 gate 语料只 `new int[n]`（prim），故显式补用户类数组回归。
- [x] 🎉🎉 **z42c.core 逐包 FULL-FILE byte-identical（cmp clean，29193B）= 首个自身包逐字节自举**。clean-room 验证（隔离 worktree @ HEAD 1.17/0.19，规避并行 add-reflection-generic-type-definition 的 1.18/0.20 未提交 WIP 对 C# oracle 的污染）：`xtask test compiler-z42` 全绿（[Test] 16/16 含 2 新 codegen + zbc 7/7 + zpkg 6/6 + e2e），z42c.core dual-build cmp 逐字节一致。
- [x] G16b z42c writer 版本同步 zbc 1.18 / zpkg 0.20（并行 add-reflection-generic-type-definition a4d8d0b5 bump 了 wire 格式但**显式延后 z42c writer 同步**，导致 z42c 仍 emit 1.17/0.19 → runtime(1.18) strict-pin 拒读 → gate e2e 红）。修：`ZbcFormat.z42` Minor 17→18 + 加 `Op.Typeof=0x73` 常量（z42c 暂不 emit typeof，纯版本/格式对齐）；`ZpkgWriter.z42` Minor 19→20；golden 版本字节 `zbc_tests.z42`（empty/f5/selfcheck header `010011`→`010012`）+ `zpkg_tests.z42`（`001300`→`001400`）。**gate 恢复全绿**：z42c [Test] 16/16 + e2e zbc 7/7 byte-identical + zpkg 6/6 byte-identical。
- [ ] G17 z42c.ir 逐包对账（进行中，复用 G10-G16 经验；dual-build@0.20 一致工具链下首测）：
  - **首测分歧**（z42c=166632B vs C#=164371B）：STRS −2430 / EXPT +5 / SIGS +22 / MODS +142（TSIG/IMPL/DEPS/NSPC/META 已等）。
  - [x] **G16c z42c writer 版本同步 zbc 1.19 / zpkg 0.21 + 接口最小 TYPE emit**（并行 add-reflection-interface-class-predicates `0bb46e05` 落地：接口产最小 TYPE 条目 + class_flags bit4=interface，同样显式延后 z42c writer 同步）。修：`ZbcFormat.z42` Minor 18→19；`ZpkgWriter.z42` Minor 20→21；`IrGen.z42` 加 `_interfaceDesc`（接口→最小 ClassDesc：无 base/字段，Flags=abstract|interface=17）+ 类循环后追加接口 emit 循环（镜像 C# `cu.Interfaces.Select(EmitInterfaceDesc)` Concat）；golden 版本字节 `zbc_tests.z42`（`010012`→`010013`）+ `zpkg_tests.z42`（`001400`→`001500`）。**gate 全绿 + z42c.core FULL byte-identical @ 1.19/0.21（29403B cmp clean，含 IShape 接口 TYPE 条目对齐 C#）**。
  - [x] **G17a 短路标签命名 + 结构镜像 C#**（`ExprEmitter._emitShortCircuit`/`_emitConditional`/`_emitNullCoalesce`）：z42c 原 `or_true`/`and_false`/`cond_*`/`coalesce_*` + 标签/alloc 序错位 + `&&`/`||` 缺独立 constReg+Copy → 改为统一 `_emitShortCircuit(b,isAnd)` 镜像 C# `EmitShortCircuit`（标签 `{tag}_rhs`/`_short`/`_end`、result 在标签后 left 前 alloc、short 块 constReg+Copy）；`?:`→`tern_*`、`??`→ C# `EmitBoundNullCoalesce`（alloc 序 left/null/cmp/result、nc_null/nc_end/nc_after 中途 Fresh）。**dual-build@0.20 验证：STRS extra 96→66、missing 33→3（~30 标签串对齐）**；`codegen_tests.z42` 4 golden（ternary/logical_and/logical_or/null_coalesce）更新为镜像 C# 输出。**gate 全绿**。
  - [x] **G17b FQ 命名空间解析根因修复**（STRS −2442→+23，z42c.ir 164354B vs C# 164371B，仅差 17B）：`is`/`as`/static-get 引用**同包但声明在兄弟 ns 的本地类**（`ConstI32Instr`/`IrType` 声明于 `Z42.IR`，从 `Z42.IR.BinaryFormat`/ZbcInstr.z42 经 `using Z42.IR;` 引用）时，z42c `QualifyClass`/`Qualify` 误用引用方 ns；C# 经 `ImportedClassNamespaces`（含 `using` 引入的跨 ns 同包类）解析到声明 ns。**修**：① `IrDump._pkgClassNs`——构建全包「短类名→声明 ns」图（跨-zpkg imported + 包内每有 ns 的类）；类 FQ 名恒为 declNs.name 与引用方无关 → 全包共用一图、无需按 CU using 过滤；同 ns 类 declNs==currentNs 与 Qualify 等价（z42c.core 单 ns 无回归，仍 byte-identical）。`BuildPackage` 建图 + 传 `_compileCu`，后者 `gen.ImportedClassNs = classNs`。② `ExprEmitter` BoundStaticGet 改用 `QualifyClass`（非 `Qualify`，镜像 C# fix-static-field-access 用 QualifyClassName）→ `IrType.Unknown` 等 15 个静态字段名限定到 `Z42.IR`。**gate 全绿 + z42c.core 仍 byte-identical**。
  - [x] **G17c-1 `[Native]` extern → BuiltinInstr 桩函数**（port，最大残差）：z42c 原**完全无** extern/builtin codegen → `[Native("__double_to_bits")] extern` 方法被跳过（无 body）→ 缺桩函数 + `__double_to_bits` 串。Port 镜像 C# legacy `[Native("__name")]` 路径（→ BuiltinInstr 非 CallNative）：① z42c.ir 加 `BuiltinInstr`（opcode 0x51）+ ZbcInstr 编码（dst tag + name pool idx + args）+ intern；② IrGen 类成员循环检测 extern + `[Native]` attr（`_nativeIntrinsic` 取首字符串 arg）→ `_emitNativeStub`（单块 entry：BuiltinInstr(__name) + ret；ParamCount/IsStatic 镜像方法，RetType="object"/"void"、ExecMode="Interp"、ParamTypes 空→SIGS 补 "?"、MaxReg=N+1；镜像 C# EmitNativeStub + `with{IsStatic}`）。**效果：EXPT/SIGS/TSIG 全 delta 0、`__double_to_bits` 串对齐**。gate 全绿 + z42c.core 仍 byte-identical。
  - [x] **G17c-2 prim 名 canon 双路协调（已落地）**：根因——C# **两套类型名**：① **编译 IR**（SIGS/TYPE/FUNC ArrayNew）用 `Z42Type.ToString()`=**TypeRegistry 规范名**（`byte→u8`/`sbyte→i8`/`short→i16`/`ushort→u16`/`uint→u32`/`ulong→u64`；`int`/`long`/`float`/`double`/`bool`/`char`/`string` **保留源名**——非全 canon！见 `TypeRegistry.cs` 各 TypeEntry 首字段）；② **TSIG** 用 `TypeExprToString(AST)`=**原始源拼写**（`byte[]`）。**同一 ToBytes:byte[] → C# SIGS=`u8[]`(idx249) + TSIG=`byte[]`(idx692) 并存**。修：① `SymbolTable.ResolveTypeP` 加 `_canonPrim`（IR 侧规范名）；② `ExportedTypeExtractor`（TSIG）改用**原始 AST 拼写**——`sig.X.Name()` → `md.RetType/Params[i].Type/fd.Type.Dump()`（ctor RetType=占位空名故保留 `sig.Ret.Name()`="void"）。z42c `TypeExpr.Dump()` 复刻规范 type-text，与 C# `TypeExprToString`（NamedType→裸名/Generic→`N<A,B>` 无空格/Array→`[]`/Option→`?`）逐字段对齐。**效果：byte/u8[] 全消（z42c.ir STRS −1，SIGS=u8[] + TSIG=byte[] 双份对齐 C#）**；gate 全绿 + z42c.core/6 fixtures TSIG 无回归（验证 `.Dump()`≡`.Name()` 对现有非-byte 类型）。
  - [ ] **G17c-3 z42c.ir 末尾残差**（2 项已诊断）：
    - **① DEPS provider `z42.core.zpkg`(z42c) vs `z42.cli.zpkg`(C#)——环境产物非 codegen bug**：DEPS 记 ns `Std` 的 provider；`Std` 被多包声明（cli/core/io/json…13 个）。两侧均 prelude-first（z42.core="0_"首位，C# `PreludePackages.Names={z42.core}`）→ nsMap[Std]=z42.core。但 C# `PackageCompiler.BuildTarget` 第 43 行 `ScanZbcForNamespaces(BuildZbcScanDirs(), nsMap)` **额外扫仓库内 ZBC 目录**（z42c DepScan 只扫单 flat libs，不做此步）→ C# 解析到 z42.cli。**实证：C# 对同一 FLAT 目录构建仍出 z42.cli（非 libs 内容差）**。z42c 选 z42.core 语义更对（z42.core 才是 Std 核心 provider）。**判定：环境依赖差，非 z42c 真 bug，不追**（除非 User 要求精确复刻 C# 多扫行为）。
    - **② MODS −73（单点结构差，精确定位）**：逐字节 cmp 首差 @ MODS off 1078 = mod0 `ByteWriter.z42` 的某 `FieldGet`(0x60) **结果 tag z42c=Object(0x20) vs C#=I32(0x04)**；解 field 名 = **`Length`**（`ByteWriter._ensure` 的 `this._buf.Length`）。z42c 把数组 `.Length` 读成 Object 而非 int → 其后指令流发散 + z42c 长 73B。根因=`arr.Length` 特例（TypeChecker `_bindMember` 行 459 + ExprEmitter `_emitMember` 行 324）要求 `m.Target.Type() is Z42ArrayType`；**全 z42c.ir 包构建下 `this._buf`(int[] 字段) 未被识别为 Z42ArrayType → `.Length` 落 Object**。**关键：单/双文件隔离 repro（含逐字 copy `_ensure`）全部正确（Length=I32）→ 仅全 z42c.ir 多文件 `CollectAll` 上下文触发**（`this._buf` field_get tag 仍 Object/array 正确，但 `.Length` 绑定时 target 非 Z42ArrayType——疑 CollectAll 两阶段 field 类型 fixup 或某兄弟类/导入交互使 `_buf` array 类型在该绑定点退化）。需在全包上下文复现该退化点 → 独立聚焦迭代。
- [ ] G18+ 逐包推进（z42c.project … driver；cmp 逐字节）。

## 验证
- [x] G9：`xtask test compiler-z42` 全绿（byte-compare 7/7 + zpkg 6/6 无回归）
- [x] 里程碑：z42c 能编译 z42c.core 全部源（无 error，双路均成功）

## 备注
- 占用 z42c 锁（延续自举主线）。byte-identical 对账留各包能编译后再逐包验。
- workspace 约定默认 `[sources]`（成员无 [sources] 段时）= 单独的 driver/project 缺口，暂用显式 toml 绕过，后续补 workspace build 编排。
