# Design: `params` 变长参数

## Architecture

`params` 是**纯编译器前端特性**：Lexer → Parser → TypeChecker → Codegen 全程在 z42c 内解决，
VM / IR / zbc 字节码层完全不感知。运行期看到的永远是"一个数组参数"。

```
源码                      z42c 前端处理                          VM 看到
─────────────────────────────────────────────────────────────────────
params string[] parts  →  Param.IsParams=true               →  普通 string[] 形参
                          Z42FuncType.ParamsFrom = idx

Join(a, b, c)          →  TypeChecker expanded form:         →  ArrayNewLitInstr + CALL
（expanded form）          trailing args → BoundArrayLit[a,b,c]    （= 普通数组字面量）
                          插到 params 形参位

Join(arr)              →  TypeChecker normal form:           →  CALL（直传 arr）
（normal form）            arr 直绑到 params 形参，不打包

Write("f", 1, "x")     →  object[] expanded: box 各实参      →  ArrayNewLitInstr（object 槽
（object[]）               （boxing = no-op，Value 自带 tag）       直持 I64/Ref Value）+ CALL
```

**关键复用**：expanded form 的"打包"不是新机制——TypeChecker 把 trailing 实参重写成既有的
`BoundArrayLit`（TypeChecker.z42:804 已有该节点），ExprEmitter 既有
`BoundArrayLit → ArrayNewLitInstr`（ExprEmitter.z42:101）原样 emit。**Codegen 零新增分支**。
`object[]` 元素的装箱借 `add-boxing-conversions` 方案 A：object 槽直接持 tagged `Value`，无 Box opcode。

## Decisions

### D1: lowering 发生在 TypeChecker 绑定期，不在 Codegen

**问题**：expanded form 的数组打包在哪一层做？
**选项**：A — TypeChecker 绑定期把 trailing args 重写成 `BoundArrayLit`；
B — Codegen 在 emit CALL 时特判 params 再插数组指令。
**决定**：**A**。理由：
- TypeChecker 已持有完整类型信息（element type 推断、box 可赋值校验、重载决议都在此层），
  打包决策天然属于绑定期；
- 产出标准 `BoundArrayLit` 后，**Codegen / ExprEmitter 完全不需要改**（复用既有数组字面量 emit）——
  符合"零新 IR、最小 codegen 改动"目标；
- 与既有 `_withDefaults`（绑定期填默认参数，TypeChecker.z42:1511）同构——params 展开是其姊妹逻辑。

### D2: 跨包 params → TSIG 加 `paramsFrom` 字节 → zpkg minor bump

**问题**：motivating API `Path.Join` 在 stdlib，被**别的 zpkg** 调用。调用方编 `Path.Join(a,b,c)`
时必须知道 `Path.Join` 末参是 `params` 才能决定打包。该信息从被调方的 imported TSIG 读取——
但当前 TSIG **不携带** per-param 修饰（`IsRef` 也未跨包编码）。
**选项**：A — TSIG 每个方法/函数记录追加一字节 `paramsFrom`（params 形参 0-based 索引，0xFF=无）→
zpkg minor 格式 bump；B — 不支持跨包 params，仅同包可用。
**决定**：**A**。理由：
- 核心用例（`Path.Join` / `Console.Write`）本质跨包，B 等于砍掉主要价值；
- 编码极小：每方法 1 字节，复用既有 `MinArgCount`/`ParamCount` 之后的位置；
- z42c 是唯一编译器（C# 已删），无 byte-identical 对账负担；格式 bump 走 strict-pin
  （z42vm major+minor 精确匹配），按 version-bumping.md checklist 同步。
> **修正 Deferred 笔记**：language-overview.md `params-future-impl` 写"零 zbc/zpkg 格式 bump"
> 仅对**同包**调用成立。跨包必须 bump zpkg minor。"零新 IR opcode / VM 不感知"仍然成立。

**`paramsFrom` 编码**：`0..ParamCount-1` = params 形参索引（约束保证 = ParamCount-1，即末参）；
`0xFF` = 无 params。写在每个 free-function / method TSIG 记录的 `ParamCount` 块之后。

### D3: 约束——`params` 只在末参、类型必 `T[]`、与 ref/out/default 互斥

**决定**：parser + TypeChecker 双重校验，违反即报错（pre-1.0 不留宽松路径）：
- `params` 必须修饰**最后一个**形参（其后不得再有形参）→ 否则 `E_PARAMS_NOT_LAST`；
- 形参声明类型必须是数组 `T[]` → 否则 `E_PARAMS_NOT_ARRAY`；
- `params` 不得与 `ref`/`out`/默认值同时出现 → 否则 `E_PARAMS_MODIFIER_CONFLICT`。

（错误码措辞最终以实现期 diagnostics 表为准，此处占位。）

### D4: 重载决议——normal form 优先 expanded form

**决定**（对齐 C# §Overload Resolution，最小子集）：
1. **normal form 优先**：调用实参恰好是单个可直绑到 params 形参的 `T[]` → 选 normal form
   （不打包），优先于把它当 expanded form 的单元素包裹；
2. **arity 校验**：expanded form 要求实参数 ≥ required（params 前的必填形参数）；params 形参吸收
   0..N 个 trailing 实参；
3. **更具体 element type 胜**：`f(1, "x")` 同时匹配 `params string[]` 与 `params object[]` 时——
   `string` 不接受 `int` → `params string[]` 不适配，fallthrough 到 `params object[]`；
   `f("a", "b")` 两者皆适配 expanded form → `string[]`（精确）胜 `object[]`（装箱加宽）；
4. **非 params 重载优先 params 重载**：存在精确 arity 的非变长重载时，它优先（变长是兜底）。

### D5: 分阶段引入——本变更只落"支持"，stdlib 晚一个 nightly 再 use

**问题**：`params` 是新语法 + zpkg 新格式。若本变更同时让 stdlib（`Path.Join`）**使用** `params`，
则只有"已懂 params 的 z42c"能编 stdlib——而那个 z42c 要靠本次构建产出 → 跨版本自举死锁
（[bootstrap-seed.md](../../../.claude/rules/bootstrap-seed.md) 语言/格式维度）。
**决定**：严格两阶段：
- **本变更（阶段 1，落支持）**：z42c 加 `params` 的 lexer/parser/typecheck/codegen + TSIG writer/reader；
  **z42c 自身源码 + stdlib + xtask 仍不使用 `params`、仍产旧版 TSIG 的调用形态**。
  → 上一个 nightly 的 z42c 能编这份源码 → 产出"支持 params"的新 z42c → 发新 nightly。
- **follow-up 变更（阶段 2，落使用）**：新 nightly 发布后，才让 `Path.Join` 等 stdlib API 用
  `params`，并删除过渡用的显式多重载。
- bump z42c **自举能力版本号**（新增语法 + 新增 zpkg 格式 → +1）；改完跑 `xtask bootstrap-check`
  确认上一 nightly 仍能编当前源。

## Implementation Notes

- **签名载体**：`Z42FuncType` 加 `ParamsFrom: int`（-1=无）。`SymbolCollector` 建签名时从
  `MethodDecl.Params[last].IsParams` 填。同包调用读本地签名，跨包读 imported TSIG 回填。
- **expanded-form 绑定**（TypeChecker `_bindCall` 内，仿 `_withDefaults`）：选中 params 重载且
  判定为 expanded form 时——把 `rawArgs[paramsFrom..]` 各自 `_bindExpr` + 必要装箱，包成
  `BoundArrayLit(elems, count, elementTypeName, Z42ArrayType(elem), span)`，放入 `paramsFrom`
  槽位；其前实参正常 1:1 绑定。
- **object[] 装箱**：element type = `object` 时，每个 trailing 实参的绑定结果按 boxing 可赋值规则
  上转 object（codegen no-op）。混类型由此天然支持。
- **normal form 判定**：恰一个 trailing 实参且其类型可直接赋给 params 形参类型（`T[]`→`T[]`，或
  `string[]`→`object[]` 协变按 C# 规则——v1 先要求精确 `T[]` 匹配，数组协变留 follow-up）。
- **TSIG 字节**：`ZpkgWriter._writeMethod` / free-function 记录在 `ParamCount` 块后 `WriteU8(paramsFrom)`；
  `ZpkgReader` 对称读；版本 minor +1，strict-pin。

## Testing Strategy

- **golden（run/）**：
  - `params_varargs_expanded` — `Sum(1,2,3)` 打包路径，输出 `6`；
  - `params_varargs_normal` — `Sum(arr)` 直传，不额外打包，输出一致；
  - `params_object_mixed` — `Write("{0}{1}", 1, "x")` 混类型 box，验证 object[] 各元素 tag。
- **cross-zpkg**：`params_cross_pkg` — 被调方 zpkg 暴露 params 方法（TSIG 带 `paramsFrom`），
  调用方另一 zpkg 用 expanded form 调用 → 端到端验证跨包标记读写。
- **z42c 单元测试**：parser 识别 `params`、约束违反（非末参 / 非数组 / 与 ref 冲突）报错；
  重载决议 normal vs expanded、string[] vs object[] 选择。
- **自举**：`xtask bootstrap-check`（上一 nightly 仍能编当前源，证 D5 纪律守住）+
  `xtask test compiler`（z42c 自举不动点 gen1==gen2，TSIG 新字节不破 fixpoint）。
- **GREEN gate**：`z42 xtask.zpkg test`（cargo build + vm + cross-zpkg + lib + compiler 全 stage）。
