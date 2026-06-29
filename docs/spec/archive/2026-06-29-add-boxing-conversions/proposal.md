# Proposal: 装箱转换（primitive ↔ object）

> 状态：DRAFT（待 User 审批）
> 里程碑：0.3.11（Method.Invoke 0.3.12 的前置）
> 子系统：compiler（z42c.semantics TypeChecker/ExprEmitter）+ runtime（Convert 完备性）

## Why

非泛型 `MethodInfo.Invoke(object obj, object[] args)`（0.3.12，退役 Rust test-runner 的前置）
要求把原始类型实参放进 `object[]`、把返回值以 `object` 取回——即 **primitive ↔ object 的装箱/
拆箱转换**。当前 z42 **不允许** `object o = 5;`（prim→object 隐式转换缺失），所以 Invoke 无法表达。

**关键现实**：z42 的运行期 `Value` 已是 tagged union——`I64/F64/Bool/Char` 与 `Object/Array` 平级，
`object[]` 物理上就是 `Vec<Value>`，能直接装 `Value::I64`。且 `Convert` 指令对 **Object 源已在运行期
按动态 Value 变体解析**（ir.md §Numeric Cast：`(long)object` 模式已工作）。因此"装箱"在 z42 **不是
运行期堆装箱问题，而是类型系统问题**：补上编译期的可赋值规则即可，运行期几乎零新增。

不做（明确划走）：
- **堆包装装箱**（C#/JVM 式 `Value::Boxed`/Box-Unbox IR + 堆分配）：z42 Value union 已统一表示，
  堆包装是冗余的双重装箱，性能更差且无语义收益（详见 design D1）。
- **enum 精确装箱**：enum 当前 I64 表示，装箱丢类型精度（GetType 返 Int32）——与 enum-as-type-entity
  本身的 deferred 对齐，不在本变更解决。
- **Method.Invoke / Type.GetType**：独立后续变更 `add-method-invoke-non-generic`（0.3.12），依赖本变更。
- **数组协变**（`int[] <: object[]`）：不引入（避免 store-hole）；装箱只作用于标量赋值 + object[] 逐元素赋值。

## What Changes

1. **隐式装箱（prim → object）**：TypeChecker 允许原始类型表达式赋给 `object` 目标（变量/参数/
   object[] 元素/返回）。**codegen 无新增指令**——`Value` 已带 tag，装箱 = 寄存器原值流过（如恒等转换般 elide）。
2. **受检拆箱（object → prim）**：`(int)o` 复用现有 `Convert`（Object 源运行期按 Value 变体解析）；
   补全所有原始目标 + tag 不符抛 `InvalidCastException`、null 抛同（运行期若已覆盖则仅验证/补缺）。
3. **重载决议优先级**：装箱转换为**最低优先级**（exact > 数值加宽 > 装箱），`f(int)` 与 `f(object)`
   并存时 `f(5)` 选 `f(int)`。
4. **`object` 作可赋值顶类型**：原始类型经装箱可赋值规则 assignable 到 object（无需名义继承）。

## 前置依赖
无（纯 additive 类型系统规则 + 复用既有 Convert）。**无新语法 token、无 zbc/zpkg 格式 bump**
（装箱 codegen 无新指令，拆箱用现有 Convert）→ 不触发 bootstrap 分阶段 support-先行纪律。

## Scope（允许改动的文件）

> **实证更正（2026-06-29）**：编译器侧（prim→object 赋值 GS6、`(T)object` 拆箱、object[] 元素装箱）
> **已全部存在，零改动**。实际改动收窄为：运行期 Bool 拆箱恒等 + golden + 单测 + 文档。
> 原列的 TypeChecker.z42 / Z42Type.z42 / ExprEmitter.z42 降级为**只读核实**（未改）。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/interp/exec_value.rs` | MODIFY | `convert_value` ref-identity 块加 `(Value::Bool(_), T_BOOL)` 恒等（唯一代码改动）|
| `src/runtime/src/interp/exec_value_tests.rs` | MODIFY | 拆箱单测：bool 恒等 / int convert / char 恒等 / str→bool 不符抛 |
| `src/tests/types/box_unbox/source.z42` + `expected_output.txt` | NEW | golden：int/bool/char/object[] 装拆箱往返 |
| `docs/design/language/boxing.md` | NEW | 装箱/拆箱长期规范（语义、边界、enum 边界、不可捕获说明）|
| `docs/design/runtime/ir.md` | MODIFY | Convert 的 Object→prim 受检拆箱语义（含 Bool 恒等）补全说明 |
| `docs/roadmap.md` | MODIFY | 0.3.11 装箱机制条目打勾 + Deferred Backlog（enum 精确装箱）|

**只读核实（未改，确认已支持）**：
- `src/compiler/z42c.semantics/src/TypeChecker.z42` — GS6:1118 prim→object 已可赋值；`(T)x`→BoundConvert
- `src/compiler/z42c.semantics/src/Z42Type.z42` — object 类型表示
- `src/compiler/z42c.semantics/src/ExprEmitter.z42` — 装箱 no-op / 拆箱 emit Convert

**只读引用**：
- `docs/design/runtime/ir.md` §Numeric Cast — 现有 Convert 语义（Object 源运行期解析）
- `src/runtime/src/metadata/types.rs` — Value 变体（装箱边界判定）
- `docs/spec/changes/retire-test-runner/design.md` — 下游 Invoke 对装箱的依赖

## Out of Scope
- Method.Invoke / Type.GetType（add-method-invoke-non-generic, 0.3.12）
- enum 精确装箱；数组协变；堆包装装箱
- `==` on object 操作数语义（保持现状；如需值相等另开，见 Open Questions）

## Open Questions
- [ ] `object a == object b`（两 object 操作数）语义：值相等 / 引用相等 / 要求显式拆箱？
      → 倾向**本变更不动**（保持现状），Invoke 不需要；如下游需要另开窄变更。design 定。
- [ ] 运行期 Convert 是否已覆盖 Object→全部原始目标（i64/f64/bool/char）+ null？
      → 实施阶段 1 先核实 exec_convert 现状，仅补缺口（多半已大部覆盖）。
