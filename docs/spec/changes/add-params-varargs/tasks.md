# Tasks: `params` 变长参数

> 状态：🟡 进行中（阶段 1-3 已落地；阶段 4 起实施）| 创建：2026-06-29 | 类型：lang（完整流程）
> 子系统占用：`compiler`（src/compiler/；原 z42c 子系统已并入 compiler）—— **2026-07-01 已登记持锁**。
> 前置：boxing 0.3.11 ✅ + 自举 ✅。分阶段纪律见 design D5（本变更只落"支持"，stdlib use 归 follow-up）。

## 进度概览
- [x] 阶段 1: Lexer + AST（`params` token / `Param.IsParams`）（随 add-type-based-overloads 附带落地，commit e924236c，inert）
- [x] 阶段 2: Parser（识别 + 约束校验）（同上，e924236c）
- [x] 阶段 3: 签名载体（`Z42FuncType.ParamsFrom` + SymbolCollector）（同上，e924236c）
- [x] 阶段 4: TypeChecker（expanded 绑定 + normal/expanded 重载决议）
- [ ] 阶段 5: 跨包 TSIG（ExportedTypes + Writer/Reader + minor bump + Imported 回填）
- [ ] 阶段 6: 测试（golden + cross-zpkg + z42c 单元）
- [ ] 阶段 7: 验证（GREEN + bootstrap-check 不动点）
- [ ] 阶段 8: 文档同步 + 归档
- [ ] 阶段 9: 后续 stdlib API 迁移到 params（⚠️ 独立 follow-up change，晚一个 nightly；非本变更）

## 阶段 1: Lexer + AST
- [x] 1.1 `TokenKind.z42` 加 `Params`（e924236c）
- [x] 1.2 `Lexer.z42` `_initKeywords` 加 `_kw("params", TokenKind.Params)`（e924236c）
- [x] 1.3 `Decl.z42` `Param` 加 `bool IsParams`（构造默认 false；Dump 不变——无 params 时输出不漂移）（e924236c）

## 阶段 2: Parser
- [x] 2.1 `_parseParamList` 在形参起始识别 `params` 修饰 → 置 `Param.IsParams=true`（e924236c，Parser.z42:1326-1342）
- [x] 2.2 约束校验：非末参 → `E0206`（`ParamsNotLast`）；非数组类型 → `E0207`（`ParamsNotArray`）；
      与 `ref`/`out`/默认值同现 → `E0208`（`ParamsModifierConflict`）（e924236c，Parser.z42:1358-1361 + DiagnosticCodes.z42:18-20）

## 阶段 3: 签名载体
- [x] 3.1 `Z42Type.z42` `Z42FuncType` 加 `int ParamsFrom`（-1=无），构造/Dump 兼容（e924236c）
- [x] 3.2 `SymbolCollector.z42` 建签名时从末参 `IsParams` 填 `ParamsFrom`（e924236c，SymbolCollector.z42:630）

## 阶段 4: TypeChecker
- [x] 4.1 `_bindCall` 重载候选：标注每候选 normal/expanded 适配性 + element-type 具体度评分
      （`TypeChecker.z42` 新增 `_resolveParamsOverload`：过滤 `ParamsFrom>=0 && argCount>=ParamsFrom`，
      normal-form 精确匹配优先，expanded-form 按 element 是否为 `object` 分 specific/object 两档）
- [x] 4.2 expanded-form 绑定（仿 `_withDefaults`）：trailing args → `BoundArrayLit`（element type；
      object[] 时各实参 box 上转）插到 `ParamsFrom` 槽
      （新增 `_withParamsExpansion`；object[] boxing 确认是 codegen 零新增——`BoundArrayLit` 复用既有
      `ArrayNewLitInstr` 路径，见 `ExprEmitter.z42:101-114`）
- [x] 4.3 normal-form 判定：单 `T[]` 实参精确匹配 params 形参 → 直绑不打包
      （`_withParamsExpansion` 中 `rawArgCount==sig.ParamCount && args[pf].Type().IsAssignableTo(at)` 直接
      透传 `args`，不构造 `BoundArrayLit`）
- [x] 4.4 决议优先级：非 params 精确 arity > normal form > expanded(更具体 element) > expanded(object[])
      （`_resolveOverload` 的 `byArity` 过滤排除 params 候选，保证非 params 精确 arity 优先；
      零匹配时才落到 `_resolveParamsOverload`，其内部先试 normal-form 唯一匹配，再试 expanded-form
      specific 档，最后落 object[] 档）
      验证：`./xtask test compiler` 全绿——79+5+10+3 单元测试通过、17 `[Test]` 单元通过、e2e 全过、
      **自举不动点 7/7 包 gen1==gen2**（TypeChecker.z42 的编辑未引入字节漂移/回归；params 逻辑本身
      因 D5 纪律尚无调用点触达，功能正确性待阶段 6.5 专项单元测试覆盖）

## 阶段 5: 跨包 TSIG
- [ ] 5.1 `ExportedTypes.z42` `ExportedFuncZ` / `ExportedMethodZ` 加 `int ParamsFrom`
- [ ] 5.2 `ExportedTypeExtractor.z42` 导出时填 `ParamsFrom`
- [ ] 5.3 `ZpkgWriter.z42` 方法/函数记录 `WriteU8(paramsFrom)`（0xFF=无）+ 版本 minor +1
- [ ] 5.4 `ZpkgReader.z42` 对称读 + strict-pin 版本校验
- [ ] 5.5 `ImportedSymbolLoader.z42` 从 imported TSIG 回填 `Z42FuncType.ParamsFrom`
- [ ] 5.6 version-bumping.md checklist 全项同步（z42vm 端 strict-pin 常量、format fixture 等）

## 阶段 6: 测试
- [ ] 6.1 golden `src/tests/run/params_varargs_expanded/`
- [ ] 6.2 golden `src/tests/run/params_varargs_normal/`
- [ ] 6.3 golden `src/tests/run/params_object_mixed/`
- [ ] 6.4 cross-zpkg `src/tests/cross-zpkg/params_cross_pkg/`
- [~] 6.5 z42c 单元：parser 约束报错 + 重载决议（normal/expanded/string[]-vs-object[]）
      重载决议部分已随阶段 4 落地：`z42c.semantics/tests/typecheck/typecheck_params_tests.z42`
      （7 用例：expanded 打包 / normal 透传 / 空 trailing / 非 params 精确 arity 优先 /
      自由函数 expanded / object[] 混类型 / expanded 歧义报 E0425），`xtask test compiler` 全绿。
      parser 约束报错（E0206/E0207/E0208）单测仍缺，留待本阶段收尾时补（z42c.syntax/tests/decl 或 parser）。
- [ ] 6.6 `examples/params_varargs.z42`

## 阶段 7: 验证
- [ ] 7.1 `cargo build`（z42vm）无错
- [ ] 7.2 `xtask bootstrap-check` —— 上一 nightly z42c 仍能编当前源（证 D5 纪律）
- [ ] 7.3 `z42 xtask.zpkg test`（全 stage GREEN：vm / cross-zpkg / lib / compiler 不动点 gen1==gen2）
- [ ] 7.4 spec scenarios 逐条覆盖确认

## 阶段 8: 文档同步 + 归档
- [ ] 8.1 `docs/design/language/language-overview.md`：Deferred `params-future-impl` → 正式语法节
- [ ] 8.2 `docs/design/runtime/ir.md`：注明 params 前端 lowering、VM 不感知
- [ ] 8.3 `docs/roadmap.md` pipeline 进度表（若适用）
- [ ] 8.4 归档 + 释放 ACTIVE.md 锁

## 阶段 9: 后续 —— stdlib API 迁移到 params（独立 follow-up change，晚一个 nightly）
> ⚠️ **不属本变更**。新开 change（如 `migrate-stdlib-to-params`，占 `stdlib` 锁），逐项：
> 改签名 + 删过渡多重载 + 查并更新调用点。**两条硬约束（2026-06-29 核实）强制它必须独立分代：**
>
> 1. **自举不动点（byte-identical）**：z42c 自身调用 `Path.Join`（[z42c.driver/Main.z42](../../../src/compiler/z42c.driver/src/Main.z42#L9) 等 3 处）。
>    若与"支持"同变更迁移 `Path.Join`：gen1（旧种子 z42c 对**旧** stdlib 编 → 直接 CALL）≠
>    gen2（新 z42c 对**新** params stdlib 编 → expanded form ArrayNewLit+CALL）→ `xtask test compiler`
>    不动点门在过渡代失败。**故 z42c 自消费的 API（Path.Join）必须等"支持"先发 nightly，迁移在下一代单独做。**
>    （String.Format/Join/Concat z42c 不调用，理论可早合，但整体延后最干净，避免迁移被拆两处。）
> 2. **拓扑事实**：旧种子 z42c 只编 z42c 自身源码 → 新 z42c 编 stdlib（[xtask_stdlib.z42:81-93](../../../src/toolchain)）。
>    stdlib 的 `params` 定义由新 z42c 编，不死锁；但 z42c **调用**的 API 迁移会改 z42c 自身 call-site codegen → 破不动点（见上）。
>
> **候选 API（本次扫描确认）：**
> - [ ] `String.Join(sep, string a, string b, string c)`（z42.core/String.z42:314）→ 折进 `params string[]`（保留 `Join(sep, string[])` 作 normal form）
> - [ ] `String.Concat(a,b)` + `Concat(a,b,c)`（String.z42:322,325）→ `params string[]`
> - [ ] `String.Format(fmt, arg0)` + `Format(fmt, arg0, arg1)`（String.z42:331,334）→ `params object[]`（混类型，借 boxing）
> - [ ] `Path.Join(string a, string b)`（z42.io/Path.z42:41，仅 2-arg，调用方靠嵌套）→ `params string[]`
> - [ ] 全库二次扫描其余"同名限-arity 重载 / 编号实参 arg0/arg1"模式
>
> **调用点检查（同一 change 内）：**
> - [ ] `Path.Join(Path.Join(a,b),c)` 等嵌套塌缩为 `Path.Join(a,b,c)`（grep `Path.Join(Path.Join`）
> - [ ] 现有 `String.Join(sep,a,b,c)` / `Format(fmt,x,y)` 调用点 expanded form 源码兼容，但确认无 arity 溢出依赖
> - [ ] 删除过渡重载后全库重编，确认无 undefined（跨包 TSIG `paramsFrom` 已就位）

## 备注
- **scope 修正**：Deferred 笔记"零 zpkg 格式 bump"仅同包成立；跨包必须 minor bump（design D2）。
- **D5 分阶段**：本变更只落"支持"；阶段 9（stdlib use `params`）是独立 follow-up change，晚一个 nightly。
- 实施前重新登记 ACTIVE.md `compiler` 锁（z42c 已并入 compiler）。
