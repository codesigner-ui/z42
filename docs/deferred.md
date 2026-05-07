# 已显式延后的功能

> **本文档仅记录"实施期延后"**：spec 实施 / fix / refactor 过程中发现 Scope 外问题、经 User 裁决"v1 不实现、等前置/未来再做"的功能。
> 每条标注：来源 spec / 触发原因 / 前置依赖 / 触发条件（什么时候应该回来做）。
>
> **设计期延后**（spec proposal/design 起草期就主动决定不做的子特性）应记录在对应
> `docs/design/<feature>.md` 的 "Deferred / Future Work" 段，**不**进本文档。两类延后的判断标准与
> 记录位置见 [`.claude/rules/workflow.md`](../.claude/rules/workflow.md) "延后特性管理"。
>
> 不在此列表也不在任何 design doc 的"未做"功能可以视为没明确决策，需要新建 spec 讨论。

---

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

- **D-1b**（WeakRef ISubscription wrapper）：2026-05-04 落地，由 `spec/archive/2026-05-04-add-weak-ref-subscription-wrapper/` 实施。
  实施期间发现两个 stop-and-ask 信号：① z42 instance method group conversion `obj.Method` 完全未实现（只有 free function path），需要 Phase 1 修复（compiler 在 `EmitBoundMember` emit thunk + `MkClos([recv])`）；② WeakRef 不能强持原 handler（会反向锁住 receiver），需要 Phase 2 加 2 个新 builtin (`__delegate_fn_name` / `__make_closure`) + Phase 3 重写 WeakRef 为 (WeakHandle, fnName) 模式 + Get 时重建 Closure。CompositeRef.Mode.Weak 同款激活。验证：lapsed listener 在 `GC.ForceCollect()` 后真正失效。

- **D-8b-3 Phase 2 / add-default-generic-typeparam**（泛型 type-param `default(T)` 运行时解析）：2026-05-07 落地，由 `spec/archive/2026-05-07-add-default-generic-typeparam/` 实施。
  新 IR 指令 `DefaultOf(dst, param_index)`（opcode 0xB0），运行时读 `frame.regs[0]`（this）的 `ScriptObject.type_args[param_index]` → `default_value_for(tag)`。
  实施期阶段 7 发现重大前置缺口 → User 裁决 A：本 spec 同时落地 type_args propagation 基础设施。改造点：① `ObjNewInstr` 增 `TypeArgs` 字段，C# IrGen 在 `EmitBoundNew` 把 `Z42InstantiatedType.TypeArgs` 序列化为 type-tag 字符串列表传给 IR；② zbc/zpkg writer + reader 双端编/解码 type_args；③ Rust `ScriptObject` struct 增 `type_args: Vec<String>` 字段，interp ObjNew handler 在 alloc 后 `borrow_mut` populate；④ JIT 路径 type_args 不传（保持 `jit_obj_new` 签名不变），JIT 实例 type_args 为空 → JIT 编译的 generic-class 方法内 `default(T)` 退化为 Null（与 LoadFieldAddr 同 trade-off：interp 是真值源，JIT 简单实现）。zbc 0.8 → 0.9。E0421 gate 改为只对 unknown type 触发；generic-T 走 `DefaultOf` 路径。method-level type-param / free generic / static method on generic class 当前不支持（无 `this` 路径），运行时 graceful 退化为 Null —— 留待后续 spec 拓展 calling convention 携带 type_args。新 3 个 golden（`default_generic_param/`, `default_generic_param_pair/`, `default_generic_param_field_init/`，均带 `interp_only` 标记）+ 2 个 xUnit case 验证。

- **D-8b-1**（stdlib `MulticastException<R>` 泛型类）：2026-05-07 落地，由 `spec/archive/2026-05-07-add-multicast-exception-generic/` 实施。
  新增 `Std.MulticastException<R> : MulticastException`，字段 `Results: R[]`；构造器走 ctor delegation `: base(failures, indices, totalHandlers)` 把父类字段初始化交给 base ctor。利用 D-8b-0 shadow-only mangling 与已存在的非泛型同名 base 共存（IR-side `MulticastException$1`，源码 `MulticastException`）。实施过程中发现两处依赖修复：①`ExportedTypeExtractor` 把 `sem.Classes` 的注册键（mangled `Foo$N`）当 `ExportedClassDef.Name` emit，导致消费者 `Z42ClassType.Name` 与自身 ctor 方法键（源码裸名）不一致 → 改 emit `ct.Name`（源码裸名），消费者 `ImportedSymbolLoader` 在 `byName` 分组检测同名 + 不同 arity 时 re-apply mangle 进 importKey；② `FunctionEmitter` 的 `isCtor = method.Name == className` 在 IR 端 className mangled 时漏判（base ctor / field-init 全跳过）→ 加 `sourceClassName` 去除 `$N` 后缀再比对。MulticastFunc / MulticastPredicate 切换到泛型 `MulticastException<R>` 仍依赖 D-8b-3 Phase 2，留作独立 follow-up。

- **D-8b-0**（class arity overloading via shadow registry）：2026-05-07 落地，由 `spec/archive/2026-05-07-add-class-arity-overloading/` 实施。
  Shadow-only mangling — `Z42ClassType` 增 `IrName` + `HasArityMangle` 派生 / 字段。SymbolCollector pre-pass 2-阶段：先扫 cu.Classes 检测 same-name 不同 arity 冲突，仅冲突情况下把 generic 一方注册到 `Name$N` 槽位（HasArityMangle=true）；非冲突 generic 类（List<T> / Dictionary<K,V> 等 stdlib 泛型）保持 bare key 不变 — **零 stdlib zpkg 影响 / 零 VM 改动**。ResolveType GenericType 路径增 `Name$N` shadow lookup（fallback bare）。BindClassMethods / BindNew / ResolveCtorName / IrGen `EmitClassDesc` / `EmitMethod` / `_funcParams` registration 等关键消费点改用 IrName 派生（HasArityMangle=true 时为 `Name$N`），non-collision case 路径上不变。新 8 个 C# 单测覆盖 IrName 派生 / 注册路由 / 同 arity duplicate detection；2 个新 golden 覆盖 coexistence + method dispatch。解锁 D-8b-1 (`MulticastException<R>` stdlib) + D-8b-3 Phase 2 (generic-T `default(T)`) 的真泛型解析。

- **D-8b-3 Phase 1**（`default(T)` zero-value 表达式 — fully-resolved T）：2026-05-06 落地，由 `spec/archive/2026-05-06-add-default-expression/` 实施。
  AST `DefaultExpr(TypeExpr)` + BoundDefault；TypeChecker 解析 T + IsResolvedDefaultTarget 校验（已知 prim / class / interface / array / option / enum / func / instantiated-generic 之一），其它路径报 E0421 InvalidDefaultType。IrGen 不引入新 IR 指令，按 T 直接 emit ConstI64(0) / ConstF64(0.0) / ConstBool(false) / ConstChar('\0') / ConstNull，与 VM `default_value_for(type_tag)` 表对齐；VM 0 改动。Phase 2（generic type-param `default(R)`）独立 spec `add-default-generic-typeparam` 处理，受 IR DefaultOf opcode + 运行时 type_args 查询阻塞。

- **D-8b-2**（catch-by-generic-type 类型过滤）：2026-05-06 落地，由 `spec/archive/2026-05-06-catch-by-generic-type/` 实施。
  AST `CatchClause.ExceptionType` 早已捕获，但 BoundCatchClause 一直丢弃；FunctionEmitterStmts 写 null 退化为 wildcard，导致所有 typed `catch (T e)` 失效。修法：BoundCatchClause 增 `ExceptionTypeName: string?`，TypeChecker `TryResolveCatchType` 解析 short → FQ + 校验 Exception 派生（E0420 InvalidCatchType），IrGen 直传 IrExceptionEntry.CatchType（IR / zbc 格式无 bump）。VM 侧：interp `find_handler` 升级签名接 `type_registry` + `&Value` 三分支判定（None / `"*"` / typed via `is_subclass_or_eq_td`）；JIT 新 helper `jit_match_catch_type` + Throw 终结子生成 `catch_chain` 链式 probe，wildcard 单 entry 保留 fast-path。泛型 catch 透明 piggyback（不依赖 D-8b-0）。4 个原 `throw "string"` legacy goldens 升级为 `throw new Exception(...)` 与新 typed-catch 语义对齐。

- **fix-cross-zpkg-using-resolution**：2026-05-06 落地，由 `spec/archive/2026-05-06-fix-cross-zpkg-using-resolution/` 实施。
  根因：`25a8505 strict-using-resolution` 的 E0602 检查用 `imported.ClassNamespaces.Values` 作为 resolved namespaces 真相源，但该字段只记录"有 class 的命名空间"——L3-Impl2 的 cross-zpkg impl-only 包（`demo.greeter` 只有 `impl IGreet for Robot` 块、无 class）的命名空间被漏掉，导致 `using Demo.Greeter;` 报 E0602。修法：`ImportedSymbols` 增 `ResolvedNamespaces` 字段，从 `Load()` 中所有 activated module 的 `mod.Namespace` 收集；`TypeChecker.EmitImportDiagnostics` 改用三源 union（`ResolvedNamespaces ∪ ClassNamespaces.Values ∪ {ownNs}`）。回归测试 `UsingResolutionTests.TypeChecker_NoError_When_UsedNamespaceHasOnlyImpls` 锁定该路径。
