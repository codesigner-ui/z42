# Design: `event` 关键字（多播）+ desugar

> Spec 2 切分后只保留多播路径（2a）。单播 event 留 2b。

## Architecture

```
源码层：
  public class Button {
      public event MulticastAction<int> Clicked;     // 多播 event field
  }

  // 外部
  btn.Clicked += h1;   //  desugar → btn.add_Clicked(h1)
  btn.Clicked -= h1;   //  desugar → btn.remove_Clicked(h1)


Parser 时（与 SynthesizeClassAutoProp 同款，AST 阶段直接 emit 同伴方法）：
  IsEvent FieldDecl + 多播类型 →
    field Clicked: MulticastAction<int> = new MulticastAction<int>()  ← auto-init
    method add_Clicked(Action<int> h): IDisposable
        body: return this.Clicked.Subscribe(h);
    method remove_Clicked(Action<int> h)
        body: this.Clicked.Unsubscribe(h);


TypeChecker desugar (BindCompoundAssign 时):
  if expr is `target.X +/- handler`
  AND target.X 是 event field
  THEN emit BoundCall(target, "add_X" / "remove_X", [handler])
```

## Decisions

### Decision 1: 字段保持原名 `X`
**问题：** 合成 add/remove 时引用底层字段叫 `X` 还是 `_X`？
**决定：A（保留原名 `X`）**。原因：
- 现有 D2a/b 字段命名 normal 风格；统一
- 内部 `this.X.Subscribe(h)` 自然
- 严格 access control 2b 加；本 spec 不做即不需要重命名隐藏字段

### Decision 2: AST 阶段合成 vs SymbolCollector 阶段合成
**决定：Parser 时合成（A）**。同 `SynthesizeClassAutoProp` 模式，AST 阶段直接产 `FieldDecl + List<FunctionDecl>`。
- 与 SynthesizeClassAutoProp 完全对称
- TypeChecker / IrGen 看到的是普通 FunctionDecl，无需特判
- 调试友好（合成方法体可见于 AST）

### Decision 3: `+=` / `-=` desugar 位置 — BindCompoundAssign
- 检查 LHS 是否 MemberExpr `target.X` 且 `target.Type` 是 `Z42ClassType` 且 X 在 EventFieldNames
- 是 → 改 emit `BoundCall(BoundCallKind.Virtual, target, "add_X" / "remove_X", [rhsBound])`
- 否 → 走原有 compound assignment 路径

### Decision 4: 不严格访问控制（延后到 2b）
- 本 spec 多播 event field 仍可 `obj.X.Invoke(...)` 外部调用（与 D2a/b 现状一致）
- 严格 access control 留给 2b

### Decision 5: 单播 event 仅 keyword 通过 + 类型校验失败
- `event Action<int> X;` 解析层通过（IsEvent=true）
- SymbolCollector 检查字段类型必须是 `GenericType("MulticastAction", _)`，否则报清晰错 "single-cast event not yet supported (D2c-singlecast pending)"
- 等 2b 落地放宽

## Implementation Notes

- `Z42ClassType` 加 `IReadOnlySet<string>? EventFieldNames` 字段（null 默认）
  - SymbolCollector 在加载 IsEvent FieldDecl 时同步注册到该集合
  - BindCompoundAssign 检查 `target.Type is Z42ClassType ct && ct.EventFieldNames?.Contains(memberName) == true`
- 单播 event 类型校验：在 SymbolCollector / Parser 合成时即验证 GenericType.Name == "MulticastAction"
- D2d 后扩 MulticastFunc / MulticastPredicate 即放宽

## Testing Strategy

- 单元测试 `EventKeywordTests.cs`：~6-8 scenarios
  - parser: event keyword + modifier
  - parser 合成：add_X / remove_X 在 cu.Classes[].Methods 存在
  - 字段 auto-init 表达式正确（GenericType MulticastAction → new MulticastAction<T>()）
  - TypeCheck: `+=` / `-=` desugar 命中
  - 单播 event 报清晰错误（"not yet supported"）
- Golden test `event_keyword_multicast/source.z42`：端到端
  - 多播事件 `+=` 订阅 / `-=` 取消 / Invoke
  - 内部 invoke
- D2a / D2b / D-5 既有 golden 全 GREEN

## 与既有特性交互

- 与 D2a/b 多播 + ISubscription wrapper 路径完全兼容
- 与 D-5 Unsubscribe 直接耦合
- 与 D1 lambda / 方法组转换 —— `obj.X += h` 中 `h` 可以是 lambda / 方法组等任何 Action<T> 兼容值
