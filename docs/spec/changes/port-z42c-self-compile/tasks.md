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
- [ ] G15 DBUG line-tracking：Format() 的多行 return 语句——C# 5 条 line 条目（追踪行内子表达式换行），z42c 4 条（per-statement TrackLine 只追语句起始行）。**z42c.core 仅剩此 1 条 DBUG 差（16B）**。需行内 span 换行追踪，但有 corpus DBUG 回归风险须谨慎。
- [ ] G16+ 逐包推进（z42c.ir … driver）。

## 验证
- [x] G9：`xtask test compiler-z42` 全绿（byte-compare 7/7 + zpkg 6/6 无回归）
- [x] 里程碑：z42c 能编译 z42c.core 全部源（无 error，双路均成功）

## 备注
- 占用 z42c 锁（延续自举主线）。byte-identical 对账留各包能编译后再逐包验。
- workspace 约定默认 `[sources]`（成员无 [sources] 段时）= 单独的 driver/project 缺口，暂用显式 toml 绕过，后续补 workspace build 编排。
