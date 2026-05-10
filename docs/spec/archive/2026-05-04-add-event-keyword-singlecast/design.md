# Design: 单播 event 关键字

## Architecture

```
event Action<int> OnKey;
   ↓ SynthesizeClassEvent (single-cast 分支)
field OnKey : Action<int>?  // OptionType wrap，init null
method add_OnKey(Action<int> h): void {
    if (this.OnKey != null) throw new InvalidOperationException(...);
    this.OnKey = h;
}
method remove_OnKey(Action<int> h): void {
    if (DelegateOps.ReferenceEquals(this.OnKey, h)) this.OnKey = null;
}
```

## Decisions

### Decision 1: add_X 返回 void（不返回 IDisposable）
**问题：** 设计 line 301 说 `add_Foo` 返回 IDisposable + 自动 cleanup 闭包；
v1 实施怎么处理？
**决定：返回 void**。原因：
- 闭包 IDisposable 实现需要 stdlib `Disposable.From(Action)` 工厂或 per-event 私有 token 类，AST 模板代码量大
- 用户主要用 `-=` 取消，IDisposable 是次要
- 多播 add 返回 IDisposable 是因 MulticastAction.Subscribe 已返回；语义 carry-over 自然
- 单播加 IDisposable 留独立 follow-up（不阻塞主路径）

### Decision 2: 单播 vs 多播识别
**问题：** SynthesizeClassEvent 怎么区分？
**决定：** GenericType.Name 判断：
- 多播：`MulticastAction` / `MulticastFunc` / `MulticastPredicate`
- 单播：`Action` / `Func` / `Predicate`
- 其他：报"unsupported event type"

```csharp
private static readonly HashSet<string> SinglecastTypeNames =
    new(StringComparer.Ordinal) { "Action", "Func", "Predicate" };

bool isMulticast = MulticastTypeNames.Contains(gt.Name);
bool isSinglecast = SinglecastTypeNames.Contains(gt.Name);
if (!isMulticast && !isSinglecast)
    throw new ParseException("event field type must be Action / Func / Predicate or Multicast{Action|Func|Predicate}");
```

### Decision 3: 字段 nullable wrap
**问题：** 单播字段类型怎么表达"可为 null"？
**决定：** 用 `OptionType` 包装：`Action<int>` → `Action<int>?`
- z42 已有 OptionType AST 节点；resolution 走 `Z42OptionType`
- 初始值默认 null（OptionType 字段无显式 init 时 z42 自动 null）

### Decision 4: throw 表达式形式
**问题：** add_X body 内的 throw 怎么构造 AST？
**决定：** `new InvalidOperationException("...")` 然后 `throw`（z42 现有 syntax）。
- 需要确保 stdlib `InvalidOperationException` 已 visible（已 ship at z42.core/Exceptions/）
- `throw` 是 stmt；通过 `ThrowStmt` AST 节点构造

### Decision 5: remove_X 引用相等比较
**决定：** 用 `DelegateOps.ReferenceEquals(this.X, handler)` —— 与 D-5 + MulticastAction.Unsubscribe 同款。

## Implementation Notes

- `SynthesizeClassEvent` 重构：
  ```csharp
  if (isMulticast) { /* 现有路径 */ }
  else if (isSinglecast) { /* 新路径 */ }
  ```
- `SynthesizeInterfaceEvent` 同款分支
- 单播 add_X body 节点：
  - `IfStmt` 检查 `MemberExpr(this, fieldName) != NullLiteral`
    - then: `ThrowStmt(NewExpr(NamedType("InvalidOperationException"), [StringLiteral("...")]))`
  - 然后 `AssignExpr(MemberExpr(this, fieldName), IdentExpr("h"))`
- 单播 remove_X body 节点：
  - `IfStmt(CallExpr(MemberExpr(IdentExpr("DelegateOps"), "ReferenceEquals"), [MemberExpr(this, fieldName), IdentExpr("h")]))`
    - then: `AssignExpr(MemberExpr(this, fieldName), NullLiteral)`

## Testing Strategy

- 单元测试 `EventKeywordTests` +3-4：
  - 单播 event field 合成 add/remove + 字段 nullable
  - interface 单播 event 合成 signatures
- Golden test `event_keyword_singlecast/source.z42`：
  - `event Action<int>` 首次 += / 双绑定 throw / -= 清空
  - `event Func<int, bool>` 简单调用
- D2a/b/c/d 既有 golden 全 GREEN
