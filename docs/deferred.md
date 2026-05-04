# 已显式延后的功能

> 跟踪 spec 实施过程中被识别为"v1 不实现，等前置/未来再做"的功能。
> 每条标注：来源 spec / 触发原因 / 前置依赖 / 触发条件（什么时候应该回来做）。
>
> 不在此列表的"未做"功能可以视为没明确决策，需要新建 spec 讨论。

---

## D-1b：`Std.WeakRef<TD>` ISubscription wrapper（D-1a 上 follow-up）

- **来源**：D-1 拆分后 D-1a (`expose-weak-ref-builtin`) 2026-05-04 落地 corelib builtins + `Std.WeakHandle` 不透明类；本条留 ISubscription wrapper
- **设计文档**：`docs/design/delegates-events.md` §5.2 + §5.3
- **缺失实现**：
  - stdlib `Std.WeakRef<TD> : ISubscription<TD>` 类（基于 D-1a 的 WeakHandle）
  - delegate 内部 .Target 提取（Closure.env 的 weak 持有；FuncRef / StackClosure 退化为 strong，per design line 191）
  - 接入 D2b CompositeRef.Mode.Weak（当前是 placeholder noop）
- **前置依赖**：D-1a 已落地（提供 WeakHandle.MakeWeak / Upgrade 原料）；delegate `.Target` 暴露机制（Closure.env 通过新 builtin / 用户暂不可访问）。
- **触发条件**：用户实际遇到 lapsed-listener 内存泄漏 / GUI 长寿对象持回调场景。
- **占位**：`CompositeRef.Mode.Weak` 保留 flag 占位；用户当前传入会 noop（与 Strong 等价），不报错。

## D-2：ISubscription chain `.AsOnce()` / `.AsWeak()` 跨 generic interface impl（D2b 延后）

- **来源**：`spec/archive/2026-05-03-add-isubscription-wrapper/` design Decision 3
- **触发原因**：z42 现有 `impl<T> ISubscription<T> { ... }` 跨 generic interface 的扩展方法机制未验证。D2b v1 仅支持 `impl<T> Action<T> { public ... AsOnce() }`（具体 delegate 类型上的扩展），ISubscription 实例的链式 chain 不可用。
- **前置依赖**：验证 / 修复 z42 generic interface 上的 impl 扩展；或实现 wrapper 类自身的 fluent API（`composite.WithMode(Once)` 已可用，只是不通过 `.AsOnce()` 链式）。
- **触发条件**：D2b 后用户希望 `someSubscription.AsOnce().AsWeak()` 这样链式融合。当前用 `new CompositeRef<T>(handler, Mode.Once | Mode.Weak)` 直接构造也能达到相同结果。

## D-3：N>4 arity Action / Func（D1c 延后）

- **来源**：`spec/archive/2026-05-02-add-generic-delegates/`
- **设计文档**：`docs/design/delegates-events.md` §3.4
- **触发原因**：design 推荐 `tools/gen-delegates.z42` 脚本自动生成 Action/Func 0-16 arity，但 z42 未自举（编译器是 C#），跑 z42 脚本生成 z42 源码是循环依赖。
- **前置依赖**：z42 自举完成（编译器用 z42 实现），或独立 spec 用 C# 写代码生成。
- **触发条件**：用户实际需要 5+ arity callable（按 C# 经验罕见，<5% 场景）。
- **当前状态**：手写 0-4 arity（覆盖 95% 场景）。

## D-4：协变 / 逆变（`<in T, out R>` 等）（D1 延后）

- **来源**：`spec/archive/2026-05-02-add-delegate-type/`
- **设计文档**：`docs/design/delegates-events.md` §12 明确"推迟到 L3 后期"
- **触发原因**：协变 / 逆变涉及泛型 type-arg 关系约束，z42 当前 generic 系统未做这类规则，加进来牵扯 ImportedSymbols / RebuildFuncType / 子类型规则全链路。
- **前置依赖**：L3 后期完整 type-system 规划；与 generics.md / static-abstract-interface.md 协同。
- **触发条件**：用户大量遇到 `Func<Animal>` ↔ `Func<Dog>` 子类型替换问题。

## D-6：嵌套 delegate dotted-path 外部引用（D1a 延后）

- **来源**：`spec/archive/2026-05-02-add-delegate-type/` Open Question 1
- **触发原因**：D1a 注册嵌套 delegate 时同时写入 simple key + qualified key (`Btn.OnClick`)，但 ResolveType NamedType 路径仅消费 simple key；外部 `Btn.OnClick` dotted-path 解析需要 MemberType 协议。
- **前置依赖**：z42 类型系统对 nested-type member access (`Class.NestedType`) 的支持；当前类内引用走 simple name 已足够。
- **触发条件**：用户需要在外部用 `Btn.OnClick` 而非 `using` 后用 simple `OnClick`。
- **当前状态**：嵌套 delegate 类内部直接 `OnClick` 引用工作；外部需要先把它声明为顶层。

## D-7-residual：单播 event 的 IDisposable token + 严格 access control

- **来源**：D-7 单播 event 主体 2026-05-04 已落地（`spec/archive/2026-05-04-add-event-keyword-singlecast/`）；本条留 design line 301-304 的 `IDisposable` 返回 + 闭包 cleanup 部分 + 严格 access control
- **设计文档**：`docs/design/delegates-events.md` §6.3 + §6.5
- **缺失实现**：
  - 单播 `add_X` 返回 `IDisposable` 而非 void（设计 line 301）—— 需要 stdlib `Std.Disposable.From(Action)` 工厂或 per-event 私有 token 类
  - 严格 access control：外部 `obj.X.Invoke(...)` / `obj.X = ...` 报 E0407（多播 + 单播都缺）
- **触发条件**：用户实际需要 `using (token = btn.OnKey += h)` 风格 + 用户希望强制不让外部直接 invoke event field

## D-8b：D2d Func/Predicate 异常聚合 + MulticastException<R>

- **来源**：D2d 拆分后 D2d-2 仅 Action 路径落地（2026-05-04）；Func/Predicate 留本条
- **设计文档**：`docs/design/delegates-events.md` §7
- **触发原因**：`MulticastException<R>` 泛型版本 + Results 数组占位（成功 = 返回值，失败 = default(R)）需要类型系统支持泛型异常类继承 + R 类型的 default value computation。当前 z42 generic + exception 组合未验证。
- **前置依赖**：z42 generic class 继承非泛型 base class 跨 catch handler 子类型匹配；R 类型 default value 计算（int=0/bool=false/string=null/Class=null）。
- **触发条件**：用户实际需要 MulticastFunc / MulticastPredicate 全跑完模式（如 plug-in 系统多实现 vote 投票）。
- **缺失实现**：
  - stdlib `MulticastException<R>` 泛型继承 MulticastException（含 Results: R[]）
  - `MulticastFunc.Invoke(continueOnException=true)` 累积 Results + Failures，抛 `MulticastException<R>`
  - `MulticastPredicate.Invoke(continueOnException=true)` 同款（R=bool）

---

## 已自动归档前的"成熟 follow-up"指南

每条 deferred 项被实施时：
1. 把对应条目从本文件移入 archive spec 的"实施备注"
2. 创建 `expose-XXX-builtin` 类型的独立 spec
3. 验证 + GREEN 后归档；本文件移除该条目

---

## 已移除条目（保留备注以便溯源）

- **D-10**（13_assert string.Contains 误指 LinkedList.Contains）：2026-05-04 排查发现并不存在真实的 dispatch bug。
  现象由两层 stale artifact 叠加造成：① D-9 commit (`bddc818 fix-default-param-cross-cu`) 已修复底层默认参数路径，但 `artifacts/z42/libs/z42.core.zpkg` 没同步，VM 仍加载旧 stdlib IR；② 多个 multicast/event golden 的 `source.zbc` 也是旧编译器产出。
  根因是 `./scripts/test-vm.sh` 入口未强制重建 stdlib + golden，由 `spec/archive/2026-05-04-fix-test-vm-stale-artifacts/` 修复。条目从此移除。
