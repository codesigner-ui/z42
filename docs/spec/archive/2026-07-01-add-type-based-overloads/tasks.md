# Tasks: type-based 方法重载决议

> 状态：✅ 已完成，归档中 | 创建：2026-06-30 | 完成：2026-07-01 | 类型：lang/semantics（完整流程）
> 子系统占用：`compiler`（src/compiler/）—— 归档后释放，解锁 `add-params-varargs` 阶段 4。
> 前置：无（VM 零改动、无格式 bump）。是 `add-params-varargs` 重载决议的前置。
>
> **进展 2026-07-01**：静态 type-based 重载决议实现完成，**全 GREEN gate 通过**——
> stdlib 272/272 ✅ / vm interp 191/0 + jit 191/0 ✅ / cross-zpkg 2/2 ✅ /
> compiler 17 units + 6/6 build + e2e + **self-host 不动点 7/7 byte-identical** ✅。
> 顺带修复 z42.test/Assert 5 组 long/double 静态重载（此前 arity-only 静默丢弃）。
> 实施中纠正（写入 D1/D4）：**type-mangle 仅限静态方法**——实例方法经 VCall 按未 mangle/`$arity` 名派发
> （虚/泛型/原始接收者；VM primitive route 仅认 `.M`/`.M$arity`），mangle 会断派发
> （String.Equals(object)/Equals(string) 暴露 → dict_iter 崩 → 已修）。所有动机 API（Assert/Path.Join/
> String.Format/Join/Concat）皆静态，覆盖无虞。**实例方法 type-based 重载**作为下一步扩展（User 要求 2026-07-01）。
>
> **进展 2026-07-01（续）**：根因修复 fix（碰撞键报错 E0408）已落地+提交（commit 0d40d903），
> 见本文件备注区。随后调研确认实例方法扩展可行（D7：协议豁免名单 + VM 零改动），User 已拍板
> "按调研方案，协议名单豁免，其余全部启用"——新增阶段 8 跟踪实例方法扩展实施。

## 进度概览
- [x] 阶段 0: 前置扫描（确认现有无同名同 arity 重载 → byte-identity 假设）
- [x] 阶段 1: type-mangling + overload-group 存储
- [x] 阶段 2: OverloadResolver（决议算法）
- [x] 阶段 3: TypeChecker 24 调用点改走决议
- [x] 阶段 4: 跨包（ExportedTypeExtractor + ImportedSymbolLoader + DependencyIndex + IrGen）
- [x] 阶段 5: 测试（单元 + golden + cross-zpkg）+ examples
- [x] 阶段 6: 验证（GREEN + byte-identical 不动点）
- [x] 阶段 7: 文档同步 + 归档
- [x] 阶段 8: 实例方法 type-based 重载扩展（P3，D7，2026-07-01 新增）

## 阶段 0: 前置扫描
- [x] 0.1 全量扫描 src/libraries + src/compiler，确认无"同类同名同 arity"方法（当前会静默冲突）。
      有 → 停下报告 User（本就是 bug）；无 → byte-identity 假设成立，继续

## 阶段 1: type-mangling + overload-group 存储
- [x] 1.1 NEW `OverloadResolver.z42`：`MangleKey(name, paramTypeNames[]) → string`（D2 三档派生）
- [x] 1.2 `Z42Type.z42` `Z42ClassType` 加内存态 `OverloadGroups`（name → 键列表）+ `OverloadKeysFor(name)` 沿 base 链聚合
- [x] 1.3 `SymbolCollector.z42` 注册方法：同 arity 冲突 → mangled 键；同步填 OverloadGroups
      （unique/arity-distinct 保持 bare/`$arity`，名字不变）

## 阶段 2: OverloadResolver（决议算法）
- [x] 2.1 适用性：`Applicable(cand, argTypes)`（arity + 每位 IsAssignableTo，复用既有）
- [x] 2.2 better-conversion 偏序：精确 > 加宽/装箱；同位不可比 → 该位不可比
- [x] 2.3 `Resolve(candidates, argTypes) → ResolveResult{Key | NoMatch | Ambiguous}`
- [x] 2.4 快路径：单候选 + arity 匹配 → 直选（等价现状，零行为变化）

## 阶段 3: TypeChecker 调用点
- [x] 3.1 `_resolveOverload(symbols, ct, name, argTypes, sp)` 包装 OverloadResolver + 候选枚举 + 诊断
- [x] 3.2 替换 `_overloadKey`+`_findMethod` 的 24 处（静态/实例/自由/prim/instantiated/operator）走决议
- [x] 3.3 新增诊断码 `AmbiguousOverload`（E04xx）；no-match 复用/细化

## 阶段 4: 跨包
- [x] 4.1 `ExportedTypeExtractor.z42`：方法导出键用 mangled 名（同 arity 重载不再冲突覆盖）
- [x] 4.2 `ImportedSymbolLoader.z42`：检测 imported 同名同 arity 多条 → 按参数 TypeName 重算 mangled 键注册 + 填 OverloadGroups
- [x] 4.3 `DependencyIndex.z42`：`GetStatic`/`GetInstance` 改 type-aware（调用方决议后提供 mangled 名）
- [x] 4.4 `IrGen.z42`：IR 函数名用 mangled 键（与注册键一致）

## 阶段 5: 测试 + examples
- [x] 5.1 单元 `z42c.semantics/tests/overload_resolve/`：精确/加宽/装箱/子类/歧义/no-match/快路径
- [x] 5.2 golden `src/tests/run/overload_by_type/`
- [x] 5.3 cross-zpkg `src/tests/cross-zpkg/overload_cross_pkg/`
- [x] 5.4 `examples/method_overloads.z42`

## 阶段 6: 验证
- [x] 6.1 `cargo build`（z42vm）无错（确认无 VM 改动需求）
- [x] 6.2 `z42 xtask.zpkg test`（全 stage GREEN）
- [x] 6.3 `xtask test compiler` gen1==gen2 byte-identical（现有代码名字不变）
- [x] 6.4 spec scenarios 逐条覆盖确认

## 阶段 7: 文档 + 归档
- [x] 7.1 `docs/design/language/language-overview.md`：重载决议 arity-only → type-based
- [x] 7.2 `docs/design/compiler/compiler-architecture.md`：决议算法 + mangling + 内存索引原理
- [x] 7.3 归档 + ACTIVE.md 锁移交回 add-params-varargs（随阶段 8.9 一并执行，见下）

## 阶段 8: 实例方法 type-based 重载扩展（D7，2026-07-01；8.1a 为 2026-07-01 调研中发现的独立修复）
- [x] 8.1 `SymbolCollector.z42`：新增协议豁免名单常量（`ToString`/`Equals`/`GetHashCode`/`GetType`/
      `get_Item`/`set_Item`，**不含** `op_*`——操作符是静态方法，已被既有静态 mangle 规则覆盖，
      不需要实例协议豁免，见 design.md D7 2026-07-01 订正）；`_fillClass` 实例方法注册分支：
      豁免名单内沿用 bare/`$arity`（不变）；豁免名单外按 `(name,arity)≥2` 套用与静态相同的 mangle
- [x] 8.1a （新增，静态阶段遗留迁移空洞）`TypeChecker._bindBinary`（`TypeChecker.z42:990`）的
      `opKey` 查找从旧 `_overloadKey`/`_findMethod`（纯 arity 键）迁移到 `_resolveOverload`
      （type-mangled 决议，与其余 23 处调用点一致）——修复同 arity 多重载操作符（如
      `op_Add(Vec,Vec)` 与 `op_Add(Vec,int)`）此前派发失败的 bug
- [x] 8.2 `TypeChecker.z42`：`_checkDuplicateStaticOverloads` 泛化（改名或新增姊妹函数）覆盖实例方法——
      豁免名单外的实例方法同样做碰撞检测报 `E0408`；豁免名单内（含 `String.Equals`）不检测、不报错
- [x] 8.3 单元测试：实例方法 alias/nullable 碰撞报错（豁免名单外）；`Equals`/`get_Item` 类
      不误报（豁免名单内，即使同 arity）；同 arity 多重载 `op_Add` 端到端派发选对（验证 8.1a）；
      `typecheck_tests.z42` 追加 7 个新单测，全部 PASS（`./xtask test compiler` 79/79 typecheck）
- [x] 8.4 golden（2026-07-01 订正实际路径——`src/tests/run/` 不存在，仓库 golden 测试按
      `src/tests/README.md` 的 category flat/dir 约定组织，非本变更 Scope 之外的独立问题）：
      ① `src/tests/operators/operator_overload_multi_arity.z42`（flat）——`op_Add` 同 arity
      多重载端到端派发（验证 8.1a）+ 普通实例方法同 arity 类型重载端到端选对；
      ② `src/tests/inheritance/virtual_override_overload_by_type.z42`（flat）——virtual/override
      同 arity 重载场景，子类 override 任一签名，虚派发结果与签名一致。②首次跑 GREEN gate 时
      暴露真 bug（override RegKey 与 base 不一致 → vtable slot 不复用），已用 `_passFixupOverrides`
      修复，见 8.7 备注
- [x] 8.5 `cargo build`（z42vm）确认无需改动（D7 已论证零改动，仍需实跑确认无回归）——release 编译通过
- [x] 8.6 `./xtask test compiler` 全绿 + self-host 不动点 7/7（实例方法 mangle 是 additive，
      现有代码若无实例同 arity 碰撞应字节不变；若 String.Equals 外还有其他隐藏碰撞，扫描+人工确认）
      —— 17 单元全过 + 79/79 typecheck + e2e 全过 + self-host 7/7 packages gen1==gen2 byte-identical
- [x] 8.7 `z42 xtask.zpkg test`（全 stage GREEN：vm/cross-zpkg/stdlib/compiler）—— 重点验证
      vtable 派发未受影响（协议方法路径 + 虚方法路径）。首跑发现 2 个失败：
      ① `virtual_override_overload_by_type`（8.4②，真 bug——见下"根因修复"）；
      ② `type_overloading`（陈旧孤儿产物 `artifacts/build/tests/classes/type_overloading.zbc`，
      git 从未跟踪过该源文件，flat-mode 测试发现按 artifacts 镜像目录 glob `.zbc` 而非扫描
      `src/tests/`，孤儿产物被误判为幽灵用例；删除后消失，与本变更无关）。两者均已修复，
      复跑 `./xtask test` 全 stage GREEN（exit 0，含 vm/cross-zpkg/stdlib/compiler + self-host
      7/7 byte-identical 不动点）
- [x] 8.8 文档同步：`docs/design/compiler/compiler-architecture.md` 新增"方法重载决议：
      type-based mangling + 协议豁免名单"整节（补齐阶段 0-7 一直缺失的实现原理文档，非仅阶段 8
      切片）；顺带把过期的 Deferred 条目 `compiler-future-typed-overload-resolution` 标记
      ✅ 已修复并同步 `docs/roadmap.md` Deferred Backlog Index 索引行。**2026-07-01 补订正**：
      "virtual/override 安全性"小节原文声称"签名一致 ⟹ mangled 键自然一致"，此假设已被 8.4②
      的 GREEN gate 首跑证伪（见 8.7）——已重写为 `_passFixupOverrides` 机制的准确描述
- [x] 8.9 归档（与阶段 7 合并执行，本变更整体收尾）

## 备注
- 完成后解锁 `add-params-varargs` 阶段 4（重载决议复用本变更，见 design D6）。
- v1 不做 int→long→double 隐式数值 better-conversion 排序（并列即报歧义）；完整表入 Deferred。
- **2026-07-01 根因修复（阶段 8 GREEN gate 暴露，`_passFixupOverrides`）**：`virtual_override_overload_by_type`
  golden 测试（8.4②）首跑失败——Base 声明两个同 arity virtual 重载（`Handle(int)`/`Handle(string)`），
  Derived 只 `override` 其中一个，虚派发仍打到 Base 实现。根因：`_fillClass` 的 `wantMangle` 只看
  **当前类自身** `arityDup`，Derived 本地该 arity 只出现 1 次 → 不 mangle → 注册成裸名，与 Base 的
  mangled 键（`Handle$1$i32`）不一致；VM `merge_with_base`（`loader.rs`）按 `simple_name` 字符串裸
  匹配 slot，两端键不一致 ⟹ override 落入新槽位、Base 原槽位保留。修复：`SymbolCollector.z42` 新增
  `_passFixupOverrides` + `_findVirtualOrigin`——`_passMembers` 之后对每个 `override` 方法沿 base 链
  上溯 AST `Decl.Mods` 找到该虚方法最初（非 override）声明层的 `RegKey`，就地改写 override 方法的
  `RegKey` 对齐。`CollectAll`（跨 CU）路径要求所有 CU 的 `_passMembers` 先跑完再跑本 pass（base/derived
  跨 CU 声明序不保证）。机制详见 `compiler-architecture.md` "virtual/override 安全性"小节（8.8 已订正）。
  验证：`./xtask test compiler` 79/79 + `./xtask test` 全 stage GREEN（`virtual_override_overload_by_type`
  由 FAIL 变 PASS）。
- **2026-07-01 根因修复（fix，User 要求系统复查序列化方案）**：`SymbolCollector._fillClass` 的
  `ct.Methods.Put(regName, msym)` 对静态同 (name,arity) 重载是 first-wins 静默覆盖——若两个签名
  `Canon` 归一后撞出同一个 mangled 键（如 `F(int)` vs `F(i32)`，或 `G(string)` vs `G(string?)`），
  后者方法体被无声丢弃、零诊断。修复：`TypeChecker._bindClass` 新增 `_checkDuplicateStaticOverloads`，
  绑体前对每个静态 (name,arity)≥2 重载组重放一遍 (name,arity) 预扫 + mangled 键碰撞检测，碰撞即报
  `E0408 DuplicateDeclaration`（复用既有未用诊断码）。范围严格限 `static`——实例方法 type-mangle
  未启用（VCall 仍按未 mangle/`$arity` 名派发），`String.Equals(object?)`/`Equals(string)` 这类
  arity-only 碰撞是已知、已接受、留给"实例方法 type-based 重载"扩展处理，不在此处误报。
  设计层面同时确认并裁定：**不支持 nullable-only / alias-only 重载**（视为重复声明而非合法重载），
  不为此改键/mangle 方案。
  顺带订正 `design.md` D2/D5 的文档/代码不一致：原文写 `ImportedSymbolLoader` 对 imported 方法"按
  TSIG 参数 TypeName 重算签名键"，但实际实现是 `sym.RegKey = m.Name` **verbatim 读回**（不重算）——
  已更正文档措辞 + 补充"为何重算危险"的理由（`_hybridTypeName` 与 `Canon(Name())` 两套类型名生成
  逻辑不保证逐字符一致，重算可能产出与定义点不同的键，派发断裂）。
  新增单测 3 个（`z42c.semantics/tests/typecheck/typecheck_tests.z42`）：alias 碰撞报错 / nullable
  碰撞报错 / 真正不同类型不误报。验证：`./xtask test compiler` 全绿（73 typecheck 单测含新 3 个
  + 7/7 self-host byte-identical 不动点 + e2e 全过）。
