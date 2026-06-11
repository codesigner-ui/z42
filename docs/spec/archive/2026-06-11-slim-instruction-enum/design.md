# Design: Slim the `Instruction` enum

> 状态：DRAFT 待审

## Architecture

```
Function.body: Box<[Instruction]>   ← interp/JIT 顺序迭代的热数组
                    │
  enum Instruction { ... }          ← size = max variant
       ├─ 热小变体（保持 inline）：Add/Sub/.../Const*/Copy/ArrayGet/Set/Len/
       │   LoadLocalAddr/LoadElemAddr/Convert/DefaultOf/PinPtr/...  (≤24 B)
       └─ 冷大变体（装箱）：Call/Builtin/VCall/CallIndirect/MkClos/ObjNew/
           CallNative/CallNativeVtable/Field{Get,Set}/Static{Get,Set}/
           LoadFn{,Cached}/LoadFieldAddr/IsInstance/AsCast
                    │  Variant { #[serde(flatten)] data: Box<XxxInsn> }
                    └─ XxxInsn { 原字段 + typed_reg_serde 属性 }
```

enum size 由「最大 inline 变体」决定。装箱所有 inline-size > 24 B 的变体后，最大
inline 变体降到 ~24 B（如 `CallIndirect`-若不装箱 / `ArrayNewLit`：`Box(16)+Reg(4..8)`），
enum ≈ 32 B（含判别式 + 对齐）。

## Decisions

### Decision 1: 装箱阈值 —— 凡带 `String` 即装箱（严格 ≤32 B）

**问题**：哪些变体装箱？
**选项**：
- A（严格）：装箱所有 inline > 24 B 的变体 = **凡带 `String` 的 15 个变体**
  （单 String 也算，~32-40 B）。enum 严格 ≤ 32 B。改动面最大。
- B（务实）：只装箱多-String / String+Box 的 ~8 个最大变体。enum ≈ 40 B（仍比
  120 B 小 3×）。改动面小，但没达 review.md 的 ≤32 B 目标。
**决定**：**选 A**。review.md E2.P4 目标就是 ≤32 B；单-String 变体的装箱是纯机械
改写（同款 match-arm 改 `data.field`）；A 让规则统一（"name-carrying = boxed"），
不留「为什么这个装那个不装」的认知负担。`String → StringId`（E2.P3）是正交后续，
本变更先把布局理顺。

**装箱清单（15）**：`Call` `Builtin` `VCall` `CallIndirect`† `MkClos` `ObjNew`
`CallNative` `CallNativeVtable`† `FieldGet` `FieldSet` `StaticGet` `StaticSet`
`LoadFn` `LoadFnCached` `LoadFieldAddr` `IsInstance` `AsCast`。
† `CallIndirect`/`CallNativeVtable` 无 String 但带 `Box<[Reg]>`（~24 B 边界）——
为「凡带 Box/name 的 call 类一致装箱」纳入；若实测它们 ≤24 B 且不装也能 ≤32 B，
可留 inline（实施时按 `size_of` 实测微调，记 tasks 备注）。

### Decision 2: JSON serde —— `#[serde(flatten)]` 保持 wire format 不变

**问题**：装箱后 serde JSON 输出会变（`Call(Box<…>)` → `{op,"0":{…}}`）。
**选项**：
- A：变体写 `Call { #[serde(flatten)] data: Box<CallInsn> }`，payload struct 持原
  字段 + `typed_reg_serde`。flatten 把内层字段摊平回 `{op:"call",dst,func,args}`。
- B：确认 JSON 无消费端（grep 无 `from_str::<Instruction>`）后直接去 serde derive。
**决定**：**选 A**。保守、零风险——JSON wire format 字节不变，即便有未发现的
debug / golden 消费端也不破。（B 更简单但赌「JSON 真没人用」；A 代价仅一个 flatten
属性。）实施时验证 flatten + 字段级 `typed_reg_serde` 组合的 round-trip（加 1 个
serde JSON round-trip 单测）。

> **实施期精化（2026-06-11）**：实测后改用更干净的等价形态——**internally-tagged
> newtype `Variant(Box<XxxInsn>)`**，**不用** `#[serde(flatten)]`。原因：枚举已是
> `#[serde(tag = "op")]`，serde 对「内层为 struct 的 newtype 变体」会**自动把字段
> 摊平进 tag 对象**，故 `Call(Box<CallInsn>)` 序列化为 `{"op":"call",dst,func,args}`，
> 与旧 struct 变体逐字符相同。比 flatten 更优：① 无 `data` 包装字段名；② 避开 flatten
> 的 Content-buffering 与字段级 `typed_reg_serde` 自定义 (de)serializer 的潜在交互
> （newtype 路径直接复用内层 struct 的 derive）。observable wire format 与目标 A 完全
> 一致，故属选项 A 的实现细节精化，不改变审批边界。round-trip 单测（Call / ObjNew /
> StaticSet 三例）守门。

### Decision 3: payload struct —— 一变体一 struct，不强行共享

**问题**：15 个 struct 还是合并同形的（如 FieldGet/IsInstance 都 {dst,obj,name}）？
**决定**：**一变体一 struct**（`CallInsn`/`VCallInsn`/`FieldGetInsn`/...）。字段名
保持语义（`func`/`method`/`class_name`/`field_name` 各异），共享 struct 会逼成
泛化名（`name`）丢失可读性。15 个小 struct 是机械样板，clarity 优先。命名：
`<Variant>Insn`，与变体同 `#[derive(Debug, Serialize, Deserialize)]`。

### Decision 4: 无 zbc 格式 bump（核心约束）

zbc wire format 由 `ZbcWriter.cs`（写）+ `zbc_reader.rs`（读）独立定义；Rust
`Instruction` 是 reader 的**反序列化目标内存表示**。`zbc_reader::read_instr` 从
opcode + 字段字节构造变体——构造端从 `Instruction::Call { dst, func, args }` 改成
`Instruction::Call { data: Box::new(CallInsn { dst, func, args }) }`，**读的字节
完全一样**。故：zbc/zpkg minor **不 bump**，fixture 不 regen，z42c writer 不动，
version-bumping.md 的 5/8 步**不触发**。（实施中若发现任何字节漂移 → 立即停，说明
假设破了，重审。）

## Implementation Notes

- 用一个静态断言把目标钉死：
  ```rust
  #[test]
  fn instruction_size_is_slim() {
      assert!(std::mem::size_of::<Instruction>() <= 32,
          "Instruction = {} B (target ≤32)", std::mem::size_of::<Instruction>());
  }
  ```
- match-site 改写是机械的：`Instruction::Call { dst, func, args } =>` 变
  `Instruction::Call { data } =>` 后函数体内 `data.dst`/`data.func`/`data.args`
  （或在 arm 头 `let CallInsn { dst, func, args } = &**data;` 一次性解构，保持
  arm 体不动——**推荐**，最小化 diff + 保留原变量名）。
- 构造端（zbc_reader）：`Box::new(CallInsn { … })`。
- `Box<[Reg]>` 字段（args/captures/elems）已经是 box——它们留在 payload struct
  里不变；装箱的是「把整个 payload 放到一个 Box 后面」，不是双重 box。
- Terminator enum（`Br{label}`/`BrCond{…labels}` 带 String）：**本变更不动**——
  Terminator 是 per-block（不是 per-instruction 热数组），收益低；列入 ir.md
  Deferred 备注（`slim-terminator-future`）。

## Testing Strategy

- 单元：`instruction_size_is_slim`（≤32 B 静态断言）+ 1 个 serde JSON round-trip
  （证 flatten 保 wire format）。
- 回归：全 runtime cargo test（interp/jit 不回归）+ `z42 xtask.zpkg test vm`
  （vm goldens：interp + JIT 端到端执行结果 0 变化——这是「行为不变」的权威门）。
- 字节不变：跑 `./src/tests/zbc-format/generate-fixtures.sh`，git diff 应**无**
  delta（证无格式漂移；若有 delta → 假设破，停）。
- 不需要新 e2e（纯布局重构，无新行为）。
