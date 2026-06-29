# Design: 装箱转换（primitive ↔ object）

## Architecture

z42 运行期 `Value` 是 tagged union（原始类型与引用平级，object[] = Vec<Value>）。
装箱因此是**编译期类型系统问题**，运行期复用既有机制：

```
prim → object（装箱）：
  TypeChecker: 允许 prim 表达式赋给 object 目标（装箱可赋值规则）
  Codegen:     无指令（Value 已带 tag，object 槽直接持原始 Value）→ 寄存器原值流过
  Runtime:     object 槽就是 Value::I64/F64/Bool/Char，零堆分配

object → prim（拆箱）：
  TypeChecker: 允许 (T_prim)o 显式 cast（o:object）
  Codegen:     emit 现有 Convert（Object 源）
  Runtime:     Convert 按动态 Value 变体解析；tag 不符/null → InvalidCastException
               （ir.md §Numeric Cast 已有 "(long)object" 运行期解析 + f64→char 校验）
```

## Decisions

### D1: 不引入堆包装装箱（Approach A，非 C#/JVM 式 B）

**问题**：装箱用"类型系统层"（Value union as-is）还是"堆包装 + Box/Unbox IR"？

**决定**：**A（类型系统层）**。理由（性能 + 健全性 + 实现面三者一致）：
- **性能**：z42 Value 本就是统一 tagged union（已无条件携带 tag）。A 装箱零分配/零 GC；
  B 在已统一表示之上再堆分配一层 = 双重装箱，每次 box 一次 alloc + GC + 写屏障，拆箱多一次
  指针追逐。Method.Invoke 组 object[] 是热路径，A 全程零装箱分配。
- **健全性**：A = 加宽上转（prim→object 永远安全）+ 受检下转（object→prim 运行期查 tag 抛
  InvalidCast）。Value 始终带具体 tag，下转可靠校验 —— 与 Java Object+cast 同种健全性。
- **实现面**：A 几乎不碰运行期（拆箱复用 Convert）；B 要新 Value 变体/堆类型 + Box/Unbox
  opcode + codegen + GC 集成（lang/ir/vm 三层）。
- **唯一让 B 有意义的场景**（装箱身份、装箱可变 struct、enum 精确 tag）z42 当前都不需要。

### D2: 装箱是 codegen no-op

`Value` 已带 tag，`object o = 5` 把 `Value::I64(5)` 放进 object 槽 = 普通 move；如恒等 cast
被 elide（ir.md:127 "Identity casts elided"）。无需 Box opcode。

### D3: 拆箱复用现有 Convert（Object 源）

ir.md:129 明确 Object/Unknown 源的 Convert 由 VM 运行期按动态 Value 变体解析（保留 stdlib
`(long)object` 模式）。本变更确保覆盖**全部**原始目标（i32/i64/f32/f64/bool/char）+ null/tag
不符统一抛 `InvalidCastException`。实施阶段 1 先核实 `exec_convert` 现状，仅补缺口（多半已大部覆盖）。

### D4: 装箱边界 = 共享 Value 表示的类型（enum），与 deferred 对齐

| 装箱目标 | Value 表示 | A 是否精确 |
|---------|-----------|-----------|
| i32/i64/f32/f64/bool/char | 各自 tag，与类型 1:1 | ✅ 精确 |
| struct/record | `Value::Object(GcRef)` 带 TypeDesc | ✅ 精确（装箱=恒等，引用上转本就成立）|
| enum | I64（无独立 tag）| ⚠️ 丢精度（GetType→Int32）|

enum 不精确是**已知边界、非不健全**：enum-as-type-entity 本身 deferred；届时给 enum 局部
带-tag 装箱即可，不必现在上全套 B。

### D5: 重载决议——装箱最低优先级

转换序：精确匹配 > 数值加宽（int→long 等）> 装箱（prim→object）。`f(int)`/`f(object)`
并存调 `f(5)` 选 `f(int)`。镜像 C# 重载决议的 boxing 最低档。TypeChecker 候选打分加"装箱"档位。

### D6: 数组协变不引入

`int[] <: object[]` **不**成立（避免 Java 式 ArrayStoreException store-hole）。装箱只作用于
标量赋值与 `object[]` **逐元素**赋值（`a[i] = 5` 每个元素装箱，no-op）——正是 Method.Invoke 组参所需。

### D7: `==` on object 操作数 —— 本变更不动

两 object 操作数的相等语义保持现状（不为装箱原始值定义新相等规则）。Invoke 不需要。如下游需
值相等另开窄变更。

## Implementation Notes
- 纯 additive：TypeChecker 接受**更多**此前被拒的程序（prim→object），不改既有合法程序的行为 →
  不破坏 z42c 自编译不动点；无新语法/格式 → 不触发 bootstrap support-先行纪律。
- z42c 自身源码**不使用**新装箱（保持旧写法），仅"支持"它——符合"support 先行、晚一 release 再 use"。
- 改 z42c.semantics 后必跑 z42c 自编译不动点（gen1==gen2）+ byte-identical gate。

## Testing Strategy
- TypeChecker 单测：`object o = 5` 通过；`f(int)` vs `f(object)` 选 int；非法拆箱目标拒绝（如 bool↔string 仍 E0424）。
- Golden（src/tests/run/box_unbox/）：装箱成功 / 拆箱成功 / 错误拆箱 InvalidCastException / null 拆箱 / object[] 元素装箱往返。
- VM：`z42 xtask.zpkg test`（含 z42c 自编译 + vm goldens）全绿；新 golden interp 通过。

## Deferred
### add-boxing-future-enum-precise
- **触发原因**：enum 当前 I64 表示，装箱丢类型精度（GetType→Int32）。
- **前置依赖**：enum-as-type-entity（IsEnum，TYPE 条目）落地。
- **触发条件**：enum 作独立类型实体规划时。
- **当前 workaround**：装箱 enum 视作其底层 int；需精确类型时不经 object 中转。
