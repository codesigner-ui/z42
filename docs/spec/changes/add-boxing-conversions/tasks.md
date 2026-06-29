# Tasks: 装箱转换（primitive ↔ object）

> 状态：🟡 DRAFT（待 User 审批 + 6.5 gate）| 创建：2026-06-29
> 里程碑：0.3.11 | 子系统：compiler（z42c.semantics）+ runtime（Convert 完备性）
> 前置：无。无新语法/格式 bump。开工前查 ACTIVE.md 登记 compiler + runtime（全空闲才开）。

## 进度概览
- [ ] 阶段 1: 运行期拆箱核实（Convert Object→prim 现状 + 补缺）
- [ ] 阶段 2: TypeChecker 装箱可赋值 + 重载优先级 + 拆箱合法性
- [ ] 阶段 3: Codegen（装箱 no-op / 拆箱 emit Convert）
- [ ] 阶段 4: 测试（TypeChecker 单测 + golden）
- [ ] 阶段 5: 文档同步
- [ ] 阶段 6: GREEN 验证（含 z42c 自编译不动点）

## 阶段 1: 运行期拆箱核实
- [ ] 1.1 读 `exec_convert`（或 Convert 实现）：Object 源已覆盖哪些原始目标？null/tag 不符行为？
- [ ] 1.2 补缺：Object→{i32,i64,f32,f64,bool,char} 全覆盖；tag 不符 + null → `InvalidCastException`
- [ ] 1.3 Rust 单测：每种 Object→prim 成功 + 不符抛 InvalidCast + null 抛

## 阶段 2: TypeChecker（z42c.semantics）
- [ ] 2.1 `Z42Type`/`TypeChecker`：prim→object 装箱可赋值规则（assignable）
- [ ] 2.2 重载决议：新增"装箱"档位，优先级 = 精确 > 数值加宽 > 装箱
- [ ] 2.3 `(T_prim)o`（o:object）拆箱 cast 合法性（不放宽既有 E0424 非法 cast）
- [ ] 2.4 object[] 元素赋值 `a[i] = prim` 走装箱可赋值

## 阶段 3: Codegen（ExprEmitter）
- [ ] 3.1 装箱：no-op（原值流过，如恒等 cast elide）
- [ ] 3.2 拆箱：emit 现有 `Convert`（Object 源）

## 阶段 4: 测试
- [ ] 4.1 TypeChecker 单测：`object o = 5` 通过；`f(int)`/`f(object)` 选 int；仅 `f(object)` 选装箱；非法拆箱拒绝
- [ ] 4.2 golden `src/tests/run/box_unbox/`：装箱→拆箱往返 / 错误拆箱 InvalidCast / null 拆箱 / object[] 元素装箱

## 阶段 5: 文档
- [ ] 5.1 `docs/design/language/boxing.md`（NEW，长期规范）
- [ ] 5.2 `language-overview.md`：object 顶类型 + 装箱简述
- [ ] 5.3 `ir.md`：Convert Object→prim 受检拆箱语义补全
- [ ] 5.4 `roadmap.md`：0.3.11 打勾 + Deferred Backlog 加 add-boxing-future-enum-precise

## 阶段 6: GREEN
- [ ] 6.1 `cargo build --release`（z42vm）无错
- [ ] 6.2 `z42 xtask.zpkg test`（全 stage）—— vm goldens（含新 box_unbox）+ cross-zpkg + lib + compiler 全绿
- [ ] 6.3 **z42c 自编译不动点**（gen1==gen2）+ byte-identical gate（改了 z42c.semantics 必跑）
- [ ] 6.4 spec scenarios 逐条覆盖确认
- [ ] 6.5 ACTIVE.md 释放锁；归档

## 备注
- 纯 additive（接受更多程序），不改既有合法程序行为 → 自编译不动点不破。
- z42c 自身源码不使用新装箱（support 先行）。
- Method.Invoke / Type.GetType = 后续 add-method-invoke-non-generic（0.3.12），依赖本变更。
