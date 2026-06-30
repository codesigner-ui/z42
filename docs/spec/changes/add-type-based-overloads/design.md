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
**约束（硬）**：VM/codegen 把一批协议方法**按固定名派发**——VM `dispatch.rs` `vtable_index.get("ToString")`、
codegen `get_`/`set_`/`op_`/委托 `Invoke`/索引器 `get_Item`。**字面"全方法签名 mangle"会断这些** → 不可行。
**决定**：**B2**。完整参数类型签名**本就序列化在 TSIG**（`ExportedMethodZ.Params[i].TypeName`，精确 + 可序列化）；
重载决议**驱动于该序列化签名**（候选集来自序列化签名重建的 overload-group，非内存临时拼凑）。
方法注册键/IR 名分三档：
1. 该名下**唯一** → bare `Name`（**不变**）
2. 同名多 arity、各 arity 唯一 → `Name$arity`（**不变**）
3. 同名**同 arity 多重载**（新增）→ `Name$arity$<typesig>`（仅这些方法改名）
**收益**：现有代码（无同 arity 重载）名字逐字节不变 → 无字节漂移、无 fixture 重生、无 bootstrap 自愈窗口、
协议方法天然不受影响（它们不会同 arity 重载）。**精确性**：身份由序列化的完整签名定义，不丢失（决议看全类型）；
名字消歧只是把"同 arity 多重载"这个唯一会冲突的情形落到唯一 IR 名。

### D2: 签名键派生 + 跨包可复算

**决定**：注册键由 SymbolCollector **按冲突上下文**派生（pre-scan 检测 name→arity→count）：
- count(name)==1 → bare；name 多 arity 但该 arity 唯一 → `Name$arity`；该 (name,arity) ≥2 → `Name$arity$<typesig>`。
- `<typesig>` = 各形参 `Z42Type.Canon(type.Name())` 剥空白后用 `$` 连接（`OverloadResolver.MangleKey`）。
  用 `Canon`（归一 int≡i32 等 + 剥 nullable）保证 **imported（keyword 拼写）与本地（canonical）一致**。
- 注册键存于 `MethodSymbol.RegKey`（= Methods 映射键 = IR 名 = BoundCall 目标名）；决议返回候选的 `RegKey`，
  不重新派生（避免重算需要兄弟上下文）。
**跨包可复算**：ImportedSymbolLoader 读 imported 方法，对同名同 arity ≥2 的，按 TSIG 参数 TypeName（resolve→`Canon(Name())`）
重算同款 `Name$arity$<typesig>` 键 → 与被调方一致。TSIG 参数 TypeName 已在记录中 → **无 zbc/zpkg 格式 bump、无字节布局变**。

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
（唯一/arity-distinct 方法键不变）；ImportedSymbolLoader 对 imported 同名同 arity ≥2 按 TSIG 参数 TypeName
重算签名键注册 + 填 overload-group；DependencyIndex `GetStatic`/`GetInstance` 对存在重载的名用签名键查
（调用方决议后提供）。**zpkg 结构/version/现有字节全不变**——只有 Assert 类 TSIG 多出第二个方法键（additive）。

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
