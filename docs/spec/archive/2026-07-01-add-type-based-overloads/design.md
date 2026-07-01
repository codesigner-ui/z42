# Design: type-based 方法重载决议

## Architecture

```
调用点 obj.M(a, b)
        │
        ▼
  TypeChecker._resolveOverload(name="M", argTypes=[Ta,Tb], candidates)
        │   candidates = 类(含 base 链)中所有名为 M 的重载（overload-group 索引枚举）
        ▼
  OverloadResolver:
    1. 适用集 = { c ∈ candidates | arity 匹配 ∧ ∀i: argType[i] 可赋值到 paramType[i] }
    2. 最具体 = 适用集中 better-conversion 偏序的唯一极小元
    3. 0 适用 → no-match 诊断 ; >1 并列最优 → 歧义诊断 ; 恰 1 → 选中
        │
        ▼
  BoundCall(MethodName = 选中重载的 mangled 键)
        │
        ▼
  ExprEmitter → CallInstr/VCallInstr(目标 = mangled 键)   ← VM 按此字符串名派发（零改动）
```

VM 全程不感知重载——它只看到一堆**唯一函数名**。重载是编译期把 (name, argTypes)
解析成唯一 mangled 名的过程。

## Decisions

### D1: 精确签名序列化 + 决议驱动；IR 名仅冲突时消歧（User 决策 B2，2026-06-30）

**问题**：`Z42ClassType.Methods` 是 `StrMap`（键→单 MethodSymbol）。同 arity 重载键冲突。
**约束（硬，范围已在 D7 精确化；2026-07-01 订正）**：一小批**协议方法**按固定名派发，字面
mangle 会断派发 → 不可行，**永久豁免**（见 D7 豁免名单，逐名列出各自的真实约束来源——
不是笼统的"VM/codegen 硬编码"，`op_*` 已确认**不**属于这类约束，另见 D7 末尾说明）。
**非协议的普通实例方法不受此约束**——D7 已确认其 mangle 路径安全，纳入决议。
**决定**：**B2**。完整参数类型签名**本就序列化在 TSIG**（`ExportedMethodZ.Params[i].TypeName`，精确 + 可序列化）；
重载决议**驱动于该序列化签名**（候选集来自序列化签名重建的 overload-group，非内存临时拼凑）。
方法注册键/IR 名分三档：
1. 该名下**唯一** → bare `Name`（**不变**）
2. 同名多 arity、各 arity 唯一 → `Name$arity`（**不变**）
3. 同名**同 arity 多重载**（新增）→ `Name$arity$<typesig>`（仅这些方法改名）
**收益**：现有代码（无同 arity 重载）名字逐字节不变 → 无字节漂移、无 fixture 重生、无 bootstrap 自愈窗口、
协议方法天然不受影响（它们不会同 arity 重载）。**精确性**：身份由序列化的完整签名定义，不丢失（决议看全类型）；
名字消歧只是把"同 arity 多重载"这个唯一会冲突的情形落到唯一 IR 名。

### D2: 签名键派生 + 跨包 verbatim 读回（非重算）

**决定**：注册键由 SymbolCollector **按冲突上下文**派生（pre-scan 检测 name→arity→count）：
- count(name)==1 → bare；name 多 arity 但该 arity 唯一 → `Name$arity`；该 (name,arity) ≥2 → `Name$arity$<typesig>`。
- `<typesig>` = 各形参 `Z42Type.Canon(type.Name())` 剥空白后用 `$` 连接（`OverloadResolver.MangleKey`）。
  用 `Canon`（归一 int≡i32 等 + 剥 nullable）保证 **imported（keyword 拼写）与本地（canonical）一致**。
- 注册键存于 `MethodSymbol.RegKey`（= Methods 映射键 = IR 名 = BoundCall 目标名）；决议返回候选的 `RegKey`，
  不重新派生（避免重算需要兄弟上下文）。

**跨包：TSIG 方法名字段直接存 `RegKey`（导出端 `ExportedTypeExtractor._fromSymbol` 用 `methKey=smd.RegKey` 写入），
ImportedSymbolLoader 原样读回**（`sym.RegKey = m.Name`，`m.Name` 即 TSIG 里的 `RegKey`），**不在导入端重算**。
**实施纠正（与早期决策不同）**：曾设想导入端按 TSIG 参数 TypeName 重算 `Name$arity$<typesig>`；改为 verbatim
读回，因为重算依赖 `_hybridTypeName`（TSIG 写入用）与 `Canon(Name())`（键片段用）两套独立的类型名生成逻辑
**不保证逐字符一致**——重算可能产出与定义点不同的键，导致调用方 emit 的目标名与被调方实际注册名不匹配、
派发断裂。verbatim 读回消除这个隐患：键只在定义点计算一次，序列化进 TSIG，调用链上所有节点（定义 / 导出 /
导入 / 决议 / emit）使用同一个字符串。TSIG 参数 TypeName 已在记录中 → **无 zbc/zpkg 格式 bump、无字节布局变**。

### D3: 重载决议算法（C# 子集，最小可用）

**适用性**：候选 `c` 适用 ⟺ argCount==paramCount（params 由 add-params-varargs 扩展为 ≥）
且对每个 i：`argType[i].IsAssignableTo(paramType[i])`（含 prim→object 装箱、子类→基类、
接口实现——复用既有 `IsAssignableTo`）。
**最具体（better-conversion 偏序）**：候选 X 比 Y 更优 ⟺ 每个实参位置 X 的形参类型"不差于"Y 且至少一处"更好"：
- 精确匹配（类型相等）> 加宽（子类→基类 / prim→object 装箱）；
- 同位置两个都是加宽且不可比 → 该位置不可比。
**裁决**：适用集空 → `E04xx no matching overload`（列出候选）；适用集有唯一偏序极小 → 选中；
多个不可比的极小（并列）→ `E04xx ambiguous overload`（要求显式 cast 消歧）。
**v1 收窄**（见 proposal Out of Scope）：不实现 int→long→double 的隐式数值 better-conversion 排序——
多个数值加宽并列即报歧义，让用户显式 cast。完整数值转换表入 Deferred。

### D4: 字节影响（B2：仅同 arity 重载方法改名）

**事实（已核实）**：现有 stdlib+compiler 真·同名同 arity 重载只有 **z42.test/Assert 5 组**（long/double：
Greater/Less/GreaterOrEqual/LessOrEqual/InRange），当前被静默压成 double 版（long→double 加宽掩盖，long 版死代码）。
B2 下：
- ✅ 其余所有方法名**不变** → 无字节漂移、committed fixture 不必重生（除非某 fixture 恰含同 arity 重载——经扫描无）；
- ✅ 默认 GREEN 门、`test vm`/cross-zpkg/lib goldens 全不受影响；
- ✅ opt-in soak / CI bootstrap **无自愈窗口**（现有码名字不变；只有 Assert 5 组新增第二个键，additive）；
- ✅ **修复** Assert long 重载（此前静默丢失）→ 正好作内建验证用例。
**实施前置（tasks 0.1，已做）**：扫描确认仅 Assert 5 组（MulticastException.ToString 是双类误报，非冲突）。

### D5: 跨包经既有 TSIG，无格式 version bump、无字节布局变

**决定**：ExportedTypeExtractor 对同名同 arity ≥2 的方法按签名键（`Name$arity$<typesig>`）导出，避免冲突覆盖
（唯一/arity-distinct 方法键不变）；ImportedSymbolLoader 对 imported 方法 **verbatim 读回 TSIG 方法名字段作
`RegKey`**（不重算，理由见 D2）；DependencyIndex `GetStatic`/`GetInstance` 对存在重载的名用签名键查
（调用方决议后提供）。**zpkg 结构/version/现有字节全不变**——只有 Assert 类 TSIG 多出第二个方法键（additive）。

### D7: 实例方法 type-based 重载（协议豁免名单，2026-07-01 User 要求；2026-07-01 订正）

**问题**：实例方法此前完全不参与 mangle（D1 范围限定），`SymbolCollector` 对同 arity 实例重载
仍是 first-wins 静默覆盖——现存真实案例 `String.Equals(object?)`/`Equals(string)`（均
`[Native("__str_equals")]`），后者覆盖前者，方法体无声丢失。

**约束来源（已核实，见 `src/runtime/src/interp/exec_vcall.rs` + `src/runtime/src/metadata/loader.rs`）**：
- vtable 快路径（`exec_vcall.rs:205` `vtable_index.get(method)`）与 primitive 接收者重试路径
  （`exec_vcall.rs:138-179`，仅重试 `{class}.{method}` → `{class}.{method}$arity`，不识别类型签名）
  按**裸名**查找；`vtable_index` 的 key（`simple_name`）在 `loader.rs::merge_with_base`（line 761
  `TypeDesc::derive_simple_method_name`）从 z42c 写入 zpkg 的 qualified 函数名派生——**mangle 与否
  完全由 z42c 决定，Rust 侧无感知**。
- 普通虚方法（非协议）走相同的 `vtable_index`/`merge_with_base` 机制，key 的产生路径里没有
  硬编码字符串。**订正（阶段 8 GREEN gate 暴露，原文假设不成立）**：`Canon` 归一后签名一致
  ⟹ mangled 键一致，这个推论**只在派生类本地也独立满足 mangle 触发条件时成立**——
  `_fillClass` 的 `wantMangle` 只看当前类自身的同 arity 出现次数（`arityDup`），若 Base 声明
  两个同 arity virtual 重载而 Derived 只 override 其中一个，Derived 本地该 arity 只出现 1 次，
  不会 mangle，与 Base 的 mangled 键不一致，`merge_with_base` 按字符串裸匹配 slot 因而失配。
  已用 `SymbolCollector._passFixupOverrides` 修复（override 方法沿 base 链上溯找到虚方法最初
  声明层的 RegKey 并对齐），见 `compiler-architecture.md` "virtual/override 安全性"一节 + tasks.md
  8.7 备注。

**豁免名单逐名核实约束来源（2026-07-01 订正——原文笼统写"VM/codegen 硬编码"不准确，
`op_*` 一开始被误纳入，现逐名查实）**：

| 名字 | 约束来源 | 证据 |
|------|---------|------|
| `ToString` | **VM Rust 字面量硬编码** | `well_known_names.rs:69` `METHOD_TO_STRING`；`dispatch.rs:90` + `jit/helpers/value.rs:134` 均 `vtable_index.get("ToString")` |
| `Equals`/`GetHashCode`/`GetType` | **编译器侧 DependencyIndex 协议排除**，非 VM 硬编码 | `DependencyIndex.z42:126-127` `_isProtocol()`：跨包实例依赖索引显式排除这 3 个名字（连同 ToString）——因为每个类都从 `Object` 继承这 4 个方法，纳入全局裸名索引会产生海量碰撞；Rust 侧未发现这 3 个名字的字面量查找 |
| `get_Item`/`set_Item` | **TypeChecker 编译器侧字面量硬编码**（比 VM 更早一层） | `TypeChecker.z42:621-622,745-749,911-916` 直接 `ct.Methods.ContainsKey("get_Item")`/`.Get("get_Item")`，完全绕开 `_resolveOverload`/`_overloadKey`——mangle 后这些字面量查找直接落空 |
| `op_Add`/`op_Subtract`/... | **无约束，已从豁免名单移除** | 见下方"`op_*` 不需要协议豁免"分析 |

**`op_*` 不需要协议豁免（2026-07-01 订正）**：操作符方法（`op_Add` 等）是**静态方法**——
`TypeChecker._bindBinary`（`TypeChecker.z42:990`）把决议结果构造成 `BoundCall("static", ...)`，
走的是静态调用路径，根本不经过本次 D7 要解决的"实例方法 VM 裸名派发"这条线，因此不需要
协议豁免。

**发现的独立 bug（静态阶段遗留迁移空洞，2026-07-01）**：`_bindBinary` 目前仍用旧的
`_overloadKey`（纯 arity 键 `op_Add$2`）+ `_findMethod`，**没有**像其余 23 处调用点
那样迁移到新的 `_resolveOverload`（type-mangled 决议，D3）。静态方法的 mangle 规则已经
无差别应用于所有 `(name,arity)≥2` 的静态方法（含 `op_Add` 这类），所以如果一个类声明
2 个同 arity 的 `op_Add`，`SymbolCollector` 会把它们 mangle 成不同键，但 `_bindBinary`
仍按旧键 `op_Add$2` 查找 → 找不到 → 派发失败。这与 D7（实例方法协议豁免）无关，是
静态重载那期（已 GREEN 合并）的遗留空洞，本次一并修复（迁移 `_bindBinary` 到
`_resolveOverload`，见 tasks 8.1a）。

**决定**：实例方法 mangle 规则 = 静态方法同款"`(name,arity)≥2` 才 mangle"，**额外加一条协议
豁免名单**（硬编码常量，需与上表各自的真实约束来源同步维护）：
```
ToString, Equals, GetHashCode, GetType,     // Object 协议四件套（理由不同，见上表）
get_Item, set_Item,                         // 索引器（TypeChecker + VM 双重硬编码）
```
豁免名单内的方法**永不 mangle**，即使 `(name,arity)≥2`——这种情况维持现状（first-wins 静默覆盖，
不引入新行为；目前唯一真实碰撞案例 `String.Equals` 落在豁免名单内，维持现状）。豁免名单外的
实例方法（含 `virtual`/`override`，含操作符 `op_*`——它们走静态路径，本就不受此名单约束）
正常套用 mangle + 决议。

**为什么不需要 VM 改动**：`vtable_index` 的查找逻辑本身是字符串匹配，不关心字符串内容是否
"mangled"——只要 z42c 在豁免名单外的方法上一致地用 mangled 名注册（包括 override 链上下两端），
`merge_with_base` 的 slot 对齐逻辑天然成立；豁免名单内的方法因为从不 mangle，Rust 侧/TypeChecker
侧的字面量查找继续命中。

### D6: 解锁 params

type-based 决议落地后，`add-params-varargs` 的重载决议直接复用：params 重载作为候选参与适用性
（expanded form 把 trailing 实参视为 element type 序列）+ 最具体（`string[]` 精确 > `object[]` 装箱）。
params 变更只需在适用性/specificity 里加"params 候选可吸收变长实参"一档。

## Implementation Notes

- **OverloadResolver.z42（NEW）**：`Resolve(candidates, argTypes) → (key | NoMatch | Ambiguous)` +
  `MangleKey(name, paramTypes) → string`。纯函数式，便于单测。
- **候选枚举**：`SymbolTable` / `Z42ClassType` 加 `OverloadKeysFor(name) → string[]`（沿 base 链聚合）。
- **TypeChecker**：`_overloadKey` + 直接 `_findMethod(key)` 的 24 处 → 改为先枚举候选 + `_resolveOverload`。
  保留快路径：候选恰 1 且 arity 匹配 → 直接选（等价现状，零行为变化）。
- **诊断**：新增 `E04xx AmbiguousOverload` / 复用 `UndefinedSymbol` 表达 no-match（措辞实施期定）。

## Testing Strategy

- **单元（z42c.semantics/tests/overload_resolve）**：精确优于加宽；装箱候选；子类/接口适用；
  歧义（两个不可比加宽）报错；no-match 报错；快路径单候选不变。
- **golden（run/overload_by_type）**：`Print(int)` vs `Print(string)` vs `Print(object)` 端到端选对。
- **cross-zpkg（overload_cross_pkg）**：被调方 zpkg 暴露同 arity 重载，调用方另一 zpkg 正确解析到具体重载。
- **自举不动点**：`xtask test compiler` gen1==gen2 byte-identical（D4：现有代码名字不变）+
  `xtask bootstrap-check`（上一 nightly 仍能编当前源——本变更不引入新语法/格式，天然满足）。
- **GREEN gate**：`z42 xtask.zpkg test`（vm/cross-zpkg/lib/compiler 全 stage）。
