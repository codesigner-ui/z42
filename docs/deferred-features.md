# 已显式延后的功能

> 跟踪 spec 实施过程中被识别为"v1 不实现，等前置/未来再做"的功能。
> 每条标注：来源 spec / 触发原因 / 前置依赖 / 触发条件（什么时候应该回来做）。
>
> 不在此列表的"未做"功能可以视为没明确决策，需要新建 spec 讨论。

---

## D-1：WeakRef 弱引用订阅 wrapper（D2b 延后）

- **来源**：`spec/archive/2026-05-03-add-isubscription-wrapper/`（待归档）
- **设计文档**：`docs/design/delegates-events.md` §5.2 + §5.3
- **触发原因**：z42 GC heap trait 已有 `make_weak` / `upgrade_weak` API（`src/runtime/src/gc/heap.rs:164-167`），但 **corelib 未暴露 builtin**（`__obj_make_weak` / `__obj_upgrade_weak` 不存在），所以 stdlib `WeakRef<T>` 无法实现 `TryGet()` 升格。
- **前置依赖**：独立 spec `expose-weak-ref-builtin` —— 在 corelib 加 2 个 native intrinsic（`__obj_make_weak(Object) -> WeakHandle` / `__obj_upgrade_weak(WeakHandle) -> Object?`）+ stdlib `WeakHandle` opaque 类型。
- **触发条件**：用户实际遇到 lapsed-listener 内存泄漏 / GUI 长寿对象持回调场景。
- **占位**：`CompositeRef.Mode` 已保留 `Weak=2` flag 占位；用户当前传入会 noop（与 Strong 等价），不报错。

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

## D-7：D2c event 关键字 + `+=` / `-=` desugar

- **来源**：D2 路线 spec 拆分（D2c 还未启动）
- **设计文档**：`docs/design/delegates-events.md` §6
- **状态**：尚未 spec，等 D2b 归档后启动
- **依赖**：D2b 完成（ISubscription wrapper）

## D-8：D2d MulticastFunc / MulticastPredicate + MulticastException 异常聚合

- **来源**：D2 路线 spec 拆分（D2d 还未启动）
- **设计文档**：`docs/design/delegates-events.md` §4 + §7
- **状态**：尚未 spec，等 D2b/c 归档后启动
- **依赖**：D2b 完成

---

## 已自动归档前的"成熟 follow-up"指南

每条 deferred 项被实施时：
1. 把对应条目从本文件移入 archive spec 的"实施备注"
2. 创建 `expose-XXX-builtin` 类型的独立 spec
3. 验证 + GREEN 后归档；本文件移除该条目
