# z42.core/src

## 职责

z42 隐式 prelude 的源码。VM 启动时无条件加载；用户项目**不可**显式声明依赖。

`sources.include` 走默认 `src/**/*.z42` 递归通配，子目录自动拾取。

## 目录与子目录职责

| 路径 | 内容 |
|------|------|
| `Object.z42` | 所有引用类型的基类；`ToString` / `Equals` / `GetHashCode` 协议方法 |
| `Type.z42` | 运行时类型对象（`typeof` 结果） |
| `String.z42` | `string` primitive 的成员方法（`Substring` / `Contains` / 等）|
| `Primitives/` | 6 个数值/布尔 primitive 的成员方法（Bool / Char / Int / Long / Float / Double） |
| `Delegates/` | callable + multicast + 订阅策略整套（详见 `docs/design/delegates-events.md`）<br>• `Delegates.z42` / `DelegateOps.z42` — base Action/Func/Predicate + `==` / `!=`<br>• `MulticastAction/Func/Predicate.z42` — 多播容器<br>• `ISubscription.z42` + `SubscriptionRefs.z42` + `WeakHandle.z42` — 订阅策略 wrapper |
| `Protocols/` | 接口契约集中：IEquatable / IComparable / IDisposable / IFormattable / INumber / IEnumerable / IEnumerator / IComparer / IEqualityComparer |
| `Exceptions/` | `Exception` 基类 + 11 个标准子类（`AggregateException` / `MulticastException` / `ArgumentException` 等）|
| `Collections/` | 基础泛型集合：`List<T>` / `Dictionary<K,V>` / `KeyValuePair<K,V>` |
| `Convert.z42` | `Convert.ToInt32` / `ToDouble` / `ToString` 等转换辅助 |
| `Assert.z42` | `Assert.Equal` / `True` / `Null` 等运行时断言 |
| `GC.z42` | `Std.GC.*` —— Collect / GetMemorySize / SetMode 等 |
| `Disposable.z42` | `IDisposable` 的通用实现 + `Disposable.From(Action)` 工厂；用于单播 event token、`SubscribeScoped` 返回值等 |

## 设计原则

详见 [src/libraries/README.md](../../README.md)：
- **Script-First**：尽可能脚本实现；extern 仅限 syscall / libm / GC barrier / 类型元数据 / UTF-8 codepoint / 数值字面量 parse
- **VM extern 集中在 z42.core**：其他 stdlib 包不允许声明 extern（z42.io 是唯一 host FFI 例外）

## 跨目录依赖（包内 forward ref，无环约束）

| 子目录 | 依赖（同包内）|
|--------|------------|
| Object / Type / String | 无 |
| Primitives | Object（实现 ToString 等）+ Protocols（实现 IEquatable / IComparable / INumber）|
| Delegates | Object + Protocols (IDisposable for Subscribe token) + Exceptions (MulticastException) |
| Protocols | Object（接口的"被实现者"）|
| Exceptions | Object + Collections (MulticastException.Failures) |
| Collections | Object + Protocols (IEnumerable / IEqualityComparer) |
| Convert / Assert / GC | Object + Exceptions（抛 ArgumentException 等）|
| Disposable | Object + Protocols (IDisposable) + Delegates (Action) |

> 同包内 forward ref 由编译器处理，**不构成实际循环**。"层级"仅作组织约定。
> 跨包 DAG 严格性见 [docs/design/stdlib-organization.md](../../../docs/design/stdlib-organization.md)。
