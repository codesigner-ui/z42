# Tasks: type-based 方法重载决议

> 状态：🟡 进行中（静态重载已实现+全 GREEN；实例重载 + 测试/归档待续）| 创建：2026-06-30 | 类型：lang/semantics（完整流程）
> 子系统占用：`compiler`（src/compiler/）—— 由 add-params-varargs 移交（params 阻塞于本变更）。
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

## 进度概览
- [ ] 阶段 0: 前置扫描（确认现有无同名同 arity 重载 → byte-identity 假设）
- [ ] 阶段 1: type-mangling + overload-group 存储
- [ ] 阶段 2: OverloadResolver（决议算法）
- [ ] 阶段 3: TypeChecker 24 调用点改走决议
- [ ] 阶段 4: 跨包（ExportedTypeExtractor + ImportedSymbolLoader + DependencyIndex + IrGen）
- [ ] 阶段 5: 测试（单元 + golden + cross-zpkg）+ examples
- [ ] 阶段 6: 验证（GREEN + byte-identical 不动点）
- [ ] 阶段 7: 文档同步 + 归档

## 阶段 0: 前置扫描
- [ ] 0.1 全量扫描 src/libraries + src/compiler，确认无"同类同名同 arity"方法（当前会静默冲突）。
      有 → 停下报告 User（本就是 bug）；无 → byte-identity 假设成立，继续

## 阶段 1: type-mangling + overload-group 存储
- [ ] 1.1 NEW `OverloadResolver.z42`：`MangleKey(name, paramTypeNames[]) → string`（D2 三档派生）
- [ ] 1.2 `Z42Type.z42` `Z42ClassType` 加内存态 `OverloadGroups`（name → 键列表）+ `OverloadKeysFor(name)` 沿 base 链聚合
- [ ] 1.3 `SymbolCollector.z42` 注册方法：同 arity 冲突 → mangled 键；同步填 OverloadGroups
      （unique/arity-distinct 保持 bare/`$arity`，名字不变）

## 阶段 2: OverloadResolver（决议算法）
- [ ] 2.1 适用性：`Applicable(cand, argTypes)`（arity + 每位 IsAssignableTo，复用既有）
- [ ] 2.2 better-conversion 偏序：精确 > 加宽/装箱；同位不可比 → 该位不可比
- [ ] 2.3 `Resolve(candidates, argTypes) → ResolveResult{Key | NoMatch | Ambiguous}`
- [ ] 2.4 快路径：单候选 + arity 匹配 → 直选（等价现状，零行为变化）

## 阶段 3: TypeChecker 调用点
- [ ] 3.1 `_resolveOverload(symbols, ct, name, argTypes, sp)` 包装 OverloadResolver + 候选枚举 + 诊断
- [ ] 3.2 替换 `_overloadKey`+`_findMethod` 的 24 处（静态/实例/自由/prim/instantiated/operator）走决议
- [ ] 3.3 新增诊断码 `AmbiguousOverload`（E04xx）；no-match 复用/细化

## 阶段 4: 跨包
- [ ] 4.1 `ExportedTypeExtractor.z42`：方法导出键用 mangled 名（同 arity 重载不再冲突覆盖）
- [ ] 4.2 `ImportedSymbolLoader.z42`：检测 imported 同名同 arity 多条 → 按参数 TypeName 重算 mangled 键注册 + 填 OverloadGroups
- [ ] 4.3 `DependencyIndex.z42`：`GetStatic`/`GetInstance` 改 type-aware（调用方决议后提供 mangled 名）
- [ ] 4.4 `IrGen.z42`：IR 函数名用 mangled 键（与注册键一致）

## 阶段 5: 测试 + examples
- [ ] 5.1 单元 `z42c.semantics/tests/overload_resolve/`：精确/加宽/装箱/子类/歧义/no-match/快路径
- [ ] 5.2 golden `src/tests/run/overload_by_type/`
- [ ] 5.3 cross-zpkg `src/tests/cross-zpkg/overload_cross_pkg/`
- [ ] 5.4 `examples/method_overloads.z42`

## 阶段 6: 验证
- [ ] 6.1 `cargo build`（z42vm）无错（确认无 VM 改动需求）
- [ ] 6.2 `z42 xtask.zpkg test`（全 stage GREEN）
- [ ] 6.3 `xtask test compiler` gen1==gen2 byte-identical（现有代码名字不变）
- [ ] 6.4 spec scenarios 逐条覆盖确认

## 阶段 7: 文档 + 归档
- [ ] 7.1 `docs/design/language/language-overview.md`：重载决议 arity-only → type-based
- [ ] 7.2 `docs/design/compiler/compiler-architecture.md`：决议算法 + mangling + 内存索引原理
- [ ] 7.3 归档 + ACTIVE.md 锁移交回 add-params-varargs

## 备注
- 完成后解锁 `add-params-varargs` 阶段 4（重载决议复用本变更，见 design D6）。
- v1 不做 int→long→double 隐式数值 better-conversion 排序（并列即报歧义）；完整表入 Deferred。
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
