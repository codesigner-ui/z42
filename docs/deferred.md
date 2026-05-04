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
- **2026-05-04 重新评估**（探索后实际工作量比原描述高）：`SymbolCollector.Impls.cs:19-23` 的 switch 仅接受 `NamedType`，**不接受** generic 实例化 target（`ISubscription<T>` / `Action<T>` 都不行）。要做需 Parser/AST/SymbolCollector 三层联动，2-4 hr 真实编译器工作。Workaround `new CompositeRef<T>(h, Once|Weak)` + `.WithMode(...)` 在 CompositeRef 实例上链式已覆盖功能需求（仅美感差），**deprioritize**：等真有用户因美感受阻再启动。

## D-3：N>4 arity Action / Func（D1c 延后）

- **来源**：`spec/archive/2026-05-02-add-generic-delegates/`
- **设计文档**：`docs/design/delegates-events.md` §3.4
- **触发原因**：design 推荐 `tools/gen-delegates.z42` 脚本自动生成 Action/Func 0-16 arity，但 z42 未自举（编译器是 C#），跑 z42 脚本生成 z42 源码是循环依赖。
- **前置依赖**：z42 自举完成（编译器用 z42 实现），或独立 spec 用 C# 写代码生成。
- **触发条件**：用户实际需要 5+ arity callable（按 C# 经验罕见，<5% 场景）。
- **当前状态**：手写 0-4 arity（覆盖 95% 场景）。
- **2026-05-04 重新评估**：探索 examples/ + tests/ 确认 0 个 5+ arity 真实使用；compiler / runtime 无 per-arity 特殊路径，加 5-16 是纯机械重复。**结论：保持 deferred**。等真有用户场景；自举完成后用 z42 自身写生成器（`tools/gen-delegates.z42`）。当前不做 C# 一次性生成器，避免引入永久的"非 z42 写的 z42 stdlib 源"反向依赖。

## D-4：协变 / 逆变（`<in T, out R>` 等）（D1 延后）

- **来源**：`spec/archive/2026-05-02-add-delegate-type/`
- **设计文档**：`docs/design/delegates-events.md` §12 明确"推迟到 L3 后期"
- **触发原因**：协变 / 逆变涉及泛型 type-arg 关系约束，z42 当前 generic 系统未做这类规则，加进来牵扯 ImportedSymbols / RebuildFuncType / 子类型规则全链路。
- **前置依赖**：L3 后期完整 type-system 规划；与 generics.md / static-abstract-interface.md 协同。
- **触发条件**：用户大量遇到 `Func<Animal>` ↔ `Func<Dog>` 子类型替换问题。

## D-8b-0：generic / non-generic class arity overloading（D-8b-1 阻塞前置）

- **来源**：2026-05-04 D-8b-1 实施期间发现的结构性 type-system gap
- **触发原因**：z42 当前 class registry `_classes` 用裸 simple name（`cls.Name`）作 key，**不按 generic arity 区分**。同名 class 的非泛型 / 泛型版本（`MulticastException` 与 `MulticastException<R>`）冲突，编译期报 E0408 duplicate + E0411 sealed。Delegate 注册早已用 arity-suffixed key（`Action$0` / `Action$1`），class 没跟进。
- **缺失实现**：
  - `SymbolCollector.Classes.cs` 类注册改为 arity-aware：`MulticastException` 与 `MulticastException$1` 共存
  - `ResolveType` / `ResolveGenericType` 路径接受同名不同 arity 路由
  - `IrGen` / zbc 跨 CU 序列化按 arity-suffixed full name emit + 跨 zpkg 导入对齐
  - C# 编译器侧 + Rust VM lookup 都需要更新
- **前置依赖**：z42 现有泛型实例化路径（已支持 generic class），主要是注册 + lookup 一致性
- **触发条件**：D-8b-1 阻塞解除（同名 generic / non-generic exception 类共存）；或用户在其他场景需要"同名 generic + non-generic"对（如 `Result` / `Result<T>`）

## D-8b-1：stdlib `MulticastException<R>` 类（structural foundation；阻塞 D-8b-0）

- **来源**：D-8b 原条目拆分（2026-05-04 重新评估）；本条仅"加 generic exception 类 + 不依赖类型过滤的最小集成"
- **设计文档**：`docs/design/delegates-events.md` §7
- **缺失实现**：
  - stdlib `Std.MulticastException<R>` 泛型类，继承 `Std.MulticastException`（非泛型 base 已存在），新增 `Results: R[]` 字段
  - 构造器接受 `failures / failureIndices / totalHandlers / results` 四个数组
- **不在本条范围**：MulticastFunc / MulticastPredicate 的 `continueOnException=true` 通路 —— 那需要 catch-by-type 真正生效（D-8b-2）才能让用户在 catch handler 里捕获 `MulticastException<int>` 而非 wildcard
- **2026-05-04 阻塞**：实施期间发现 z42 class registry 不区分 arity，`MulticastException` 与 `MulticastException<R>` 同名冲突；阻塞解除前必须先做 D-8b-0。规避路径（重命名为 `MulticastResultException<R>`）会留命名债 + 偏离设计，已弃

## D-8b-2：catch-by-generic-type 类型过滤（编译器 + VM）

- **来源**：D-8b 探索 2026-05-04 发现 —— `BoundCatchClause` 不携带异常类型，所有 catch 当 wildcard 处理，**`catch (MulticastException<int> e)` 无法过滤**
- **触发原因**：在 D-8b-1 落地 `MulticastException<R>` 后，用户写 `try { ... } catch (MulticastException<int> e) { ... }` 不会按类型过滤，e 会捕获所有异常 —— 类型断言假象
- **前置依赖**：
  - Parser 保留 catch clause 的 type expression（`catch (T e)` 中的 `T`）
  - TypeChecker 把类型存入 `BoundCatchClause`
  - IR `IrExceptionEntry.CatchType` 存全限定名（含 type args 序列化）
  - VM `exec_throw` / catch 分发期检查 thrown value 是否 instance-of CatchType（包括泛型子类匹配）
- **触发条件**：D-8b-1 落地后，用户尝试 catch generic exception 子类时；或任意场景需要"按类型过滤异常"

## D-8b-3：`default(R)` 表达式（IR + VM 元素初始化）

- **来源**：D-8b 探索 2026-05-04 发现 —— z42 当前没有 `default(T)` 表达式
- **触发原因**：`MulticastException<R>.Results[i]` 在某索引失败时需要占位值（int=0 / bool=false / string=null / Class=null）。手动 sentinel 不通用
- **前置依赖**：
  - Parser/AST 加 `DefaultExpr(TypeExpr)` 节点
  - TypeChecker 接受任意类型，得到该类型的 zero-init 值
  - IR codegen emit `Const0` 系列 / `ConstNull`，或加新 `DefaultOf(typeId)` 指令
  - 跨 generic param `default(T)` 在 monomorphize 期解析具体类型
- **触发条件**：D-8b-1 落地后，MulticastFunc 真要走 continueOnException 路径填 Results placeholder 时

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

- **D-6**（嵌套 delegate dotted-path 外部引用）：2026-05-04 落地，由 `spec/archive/2026-05-04-add-nested-delegate-dotted-path/` 实施。
  AST 加 `MemberType(Left, Right)` 节点，TypeParser dotted-path lookahead，SymbolTable.ResolveMemberType 拍平为 qualified key 查 Delegates。
  v1 仅支持 1 层嵌套 + 非泛型；深嵌套 / 嵌套泛型 parser 接受但 lookup 给 Unknown，留待后续 spec。

- **D-7-residual**（单播 event IDisposable token + 严格 access control）：2026-05-04 落地，由 `spec/archive/2026-05-04-add-singlecast-event-idisposable-token/` 实施。
  原 spec Decision 1 选项 B（嵌套 sealed token 类）发现依赖未实现的"嵌套 class"基础设施，切换到选项 A：新增 stdlib `Std.Disposable : IDisposable` + `Disposable.From(Action)` 工厂。单播 `add_X` body 末尾 return `Disposable.From(() => this.remove_X(h))` 通过 lambda 捕获 owner+handler。新 diagnostic 码 E0414 EventFieldExternalAccess（在 BindMemberExpr 单点检查 EventFieldNames + insideClass）；多播单播双路径同样生效。
