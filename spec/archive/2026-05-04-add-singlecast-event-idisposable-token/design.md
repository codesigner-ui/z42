# Design: 单播 event IDisposable token + access control（D-7-residual）

## Architecture

```
Parser (TopLevelParser.Members.cs)
   │
   ├── 多播 event field：合成 add_X (IDisposable return) → MulticastAction<T>.Subscribe(h)
   │
   └── 单播 event field：合成 add_X (IDisposable return) → 二次 add throw, 否则
                          ├── 合成嵌套 sealed class Disposable_X (IDisposable)
                          │     └── Dispose() → owner.remove_X(this.h)
                          └── add_X body: this._X = h; return new Disposable_X(this, h);
                          
TypeChecker (TypeChecker.Exprs.Members.cs / Stmts.cs)
   │
   ├── FieldAccess：若 field ∈ EventFieldNames 且不在 owner class → 检查 parent expr
   │   ├── parent 是 CallExpr (.Invoke / 直接 call) → 报 E0414
   │   └── parent 是 Assign LHS → 报 E0414
   │
   └── 不影响 += / -= desugar（已转 add_X / remove_X 调用）
```

## Decisions

### Decision 1：单播 token 用嵌套类而非 `Std.Disposable.From(Action)` 工厂

**问题**：单播 add_X 返回 IDisposable，需要一个 IDisposable 实例，怎么造？

**选项**：
- A — stdlib 加 `Std.Disposable.From(Action onDispose)` 工厂类，单播合成调 `Disposable.From(() => this.remove_X(h))`
- B — 每个单播 event field 编译期合成一个嵌套 sealed class `Disposable_{FieldName}`，构造捕获 owner + handler，Dispose 调 remove_X

**决定**：**B**

**理由**：
- A 依赖 lambda / closure 在 stdlib 边界传值（要 Closure.env GC 持有），增加 reference cycle 风险
- B 嵌套类成员显式（owner / handler 两个字段），无 closure 捕获，VM 路径更直接
- B 与多播 `MulticastSubscription<T>` 实现模式同构（也是嵌套类风格），保持设计一致
- 唯一缺点是多 event = 多 token 类，编译期 class 数量上升；但每个类内容很小（2 字段 + 1 方法），代码膨胀可控
- 未来若有"通用 IDisposable from action"需求（独立场景），再加 `Std.Disposable.From` 工厂，本变更不预先做

### Decision 2：E0414 的触发位置在 TypeChecker，不在 Parser

**问题**：外部 `obj.X.Invoke(...)` / `obj.X = ...` 需要拒绝，在 Parser 还是 TypeChecker？

**决定**：**TypeChecker**

**理由**：
- Parser 阶段不知道字段类型信息（不知道 X 是不是 event 字段，需要符号表）
- TypeChecker 已经在 FieldAccess 处理 visibility 检查（AccessViolation E0404），E0414 复用同一个走查点逻辑层次一致
- `EventFieldNames` HashSet 在 SymbolCollector 阶段已收集完整，TypeChecker 直接 O(1) 查询

### Decision 3：单播 / 多播 access control 一并修

**问题**：deferred 条目说"多播 + 单播都缺"。本次只补单播 IDisposable，access control 是否也要把多播一起补？

**决定**：**一并修**

**理由**：
- access control 检查与 event field 是不是单播无关（都是"event field 外部调用 / 赋值"）
- 不一并修留下"多播 access control 仍 broken"的隐患，将来还要单独再起 spec
- TypeChecker 改动是同一个走查点，加 if 分支即可，工作量增量小

### Decision 4：嵌套 token 类不暴露给用户代码

**问题**：单播合成的 `Disposable_X` 类是 implementation detail，用户代码不该看到 / 引用

**决定**：把类标记为 `private`（仅 owner class 内部可见），且不进 owner class 的公开 namespace lookup

**理由**：实现细节封装；如果用户能 `new Btn.Disposable_OnKey(...)` 自己造 token，会破坏单播 single-binding 不变量

## Implementation Notes

### 单播 token 类合成模板

对于 `class Owner { public event Action<int> OnKey; }`，合成：

```z42
class Owner {
    private Action<int>? _OnKey;
    
    private sealed class Disposable_OnKey : IDisposable {
        private Owner owner;
        private Action<int> handler;
        public Disposable_OnKey(Owner o, Action<int> h) { this.owner = o; this.handler = h; }
        public void Dispose() {
            if (this.owner._OnKey == this.handler) {
                this.owner._OnKey = null;
            }
        }
    }
    
    public IDisposable add_OnKey(Action<int> h) {
        if (this._OnKey != null) throw new InvalidOperationException("...");
        this._OnKey = h;
        return new Disposable_OnKey(this, h);
    }
    
    public void remove_OnKey(Action<int> h) {
        if (this._OnKey == h) this._OnKey = null;
    }
}
```

注意：Dispose 内的 reference equality `_OnKey == this.handler` 复用 D-5 的 `__delegate_eq` builtin（已落地）。

### TypeChecker 走查点

`TypeChecker.Exprs.Members.cs` 的 FieldAccess 路径，在已有 visibility 检查之后插入：

```csharp
if (ct.EventFieldNames.Contains(m.Member) && !insideClass) {
    // Defer E0414 emission to caller context (CallExpr / Assign).
    // Mark BoundMember with `IsExternalEventAccess = true` flag.
}
```

然后在 CallExpr / AssignStmt 处理时：

```csharp
if (callee is BoundMember { IsExternalEventAccess: true }) {
    _diags.Error(DiagnosticCodes.EventFieldExternalAccess,
        $"event field `{m.Member}` cannot be invoked outside `{ct.Name}`; raise it from inside the class",
        callExpr.Span);
}
```

`+=` / `-=` desugar 应该已经把 callee 替换成 `add_X` / `remove_X` 静态方法名，不会触发 `IsExternalEventAccess` 路径。需要在实施时 verify desugar 顺序。

## Testing Strategy

- **单元测试**：
  - `EventAccessControlTests.cs` — E0414 单播外部 invoke / 单播外部 assign / 多播外部 invoke / 多播外部 assign / 类内部访问通过 / `+=` 在外部通过（不报）
- **golden test**：
  - `event_singlecast_idisposable` — `var t = btn.OnKey += h; ev.Fire(); t.Dispose(); ev.Fire();` 验证第二次 fire 不打印
- **VM 验证**：dotnet test + ./scripts/test-vm.sh
