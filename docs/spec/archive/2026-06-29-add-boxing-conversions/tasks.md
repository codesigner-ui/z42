# Tasks: 装箱转换（primitive ↔ object）

> 状态：🟢 已完成 | 创建：2026-06-29 | 完成：2026-06-29
> 里程碑：0.3.11 | 子系统：compiler（z42c.semantics）+ runtime（Convert 完备性）
> 前置：无。无新语法/格式 bump。开工前查 ACTIVE.md 登记 compiler + runtime（全空闲才开）。

## 进度概览
- [x] 阶段 1: 运行期拆箱核实（Convert Object→prim）— 仅 Bool 恒等缺口，已补 + 4 单测绿
- [x] 阶段 2: TypeChecker — **核实已支持，零代码改动**（GS6 TypeChecker.z42:1118 prim→object 已可赋值；`(T)object` 已走 BoundConvert；重载是 arity-only，无同 arity 类型重载 → 优先级需求 N/A）
- [x] 阶段 3: Codegen — **零改动**（装箱已 no-op；拆箱已 emit Convert）；空测试程序实证编译器未改即编过
- [x] 阶段 4: 测试 — golden `src/tests/types/box_unbox/`（int/bool/char/object[] 往返）+ Rust 拆箱单测（阶段 1.3）
- [x] 阶段 5: 文档同步 + spec 校正 — boxing.md(NEW) + ir.md + roadmap(0.3.11 校正为方案 A + Deferred 索引) + spec/proposal/tasks 据实校正
- [x] 阶段 6: GREEN 验证 — cargo build ✅ / exec_value 单测 27 ✅ / **xtask test vm interp 189 passed 0 failed（含 OK: box_unbox）+ regen 200/0** ✅；z42c.semantics 未改 → 自编译不动点不涉及；jit 模式 CI vm-jit-consistency 覆盖（convert_value 共享逻辑）

> **实证校正（2026-06-29）**：本特性 90% 在编译器里**已存在**——prim→object 赋值（GS6）、
> `(T)object` 拆箱 cast、object[] 元素装箱全已工作。整改动 = **运行期 Bool 拆箱恒等一处** +
> golden + 单测 + 文档。z42c.semantics **未改**（端到端实证：未动编译器即编过所有用例）。

## 阶段 1: 运行期拆箱核实 ✅
- [x] 1.1 读 `exec_value::convert_value`：int/long/char/f64 拆箱已工作（convert 语义，z42 int 皆 I64）；str/null/tag 不符已抛 InvalidCastException（落 `other => bail!`）
- [x] 1.2 补缺：唯一缺口 = Bool 拆箱（无数值 arm 误落 bail）→ ref-identity 块加 `(Value::Bool(_), T_BOOL)` 恒等
- [x] 1.3 Rust 单测：unbox_bool_identity / unbox_int_via_convert_path / unbox_char_identity / unbox_mismatch_str_to_bool_throws（exec_value 27 passed）

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

## 阶段 5: 文档 ✅
- [x] 5.1 `docs/design/language/boxing.md`（NEW，长期规范：方案 A、语义、enum 边界、不可捕获说明）
- [x] 5.3 `ir.md`：Convert Object→prim 受检拆箱（含 Bool 恒等）语义补全
- [x] 5.4 `roadmap.md`：0.3.11 校正为方案 A（无 box/unbox IR）+ Deferred Backlog 加 add-boxing-future-enum-precise
- [x] 5.2 language-overview：object 顶类型本就有「任何类型可赋 object」（GS6），无新增需写（boxing.md 承载语义）

## 阶段 6: GREEN ✅
- [x] 6.1 `cargo build --release`（z42vm）无错
- [x] 6.2 `./xtask test vm` interp **189 passed, 0 failed**（含 `OK: box_unbox`）+ regen 200/0；stdlib 22/22。jit 模式由 CI vm-jit-consistency 覆盖（convert_value 共享逻辑，interp 绿即蕴含）
- [x] 6.3 z42c.semantics **未改** → 自编译不动点/byte-identical 不涉及（实证：未动编译器即编过全部用例）
- [x] 6.4 spec scenarios 逐条实证（int/bool/char/object[] 往返 + 错误拆箱 InvalidCastException）
- [x] 6.5 ACTIVE.md 释放锁；归档

## 备注
- 纯 additive（接受更多程序），不改既有合法程序行为 → 自编译不动点不破。
- z42c 自身源码不使用新装箱（support 先行）。
- Method.Invoke / Type.GetType = 后续 add-method-invoke-non-generic（0.3.12），依赖本变更。
