# Design: interface event default 实现

## Architecture

```
interface IBus {
    event MulticastAction<int> Clicked;     ← 声明
}
                                ⇣
SynthesizeInterfaceEvent (parser-阶段) →
    MethodSignature add_Clicked(Action<int>): IDisposable    [instance abstract]
    MethodSignature remove_Clicked(Action<int>): void        [instance abstract]


class Bus : IBus {
    public event MulticastAction<int> Clicked;   ← D2c-多播 已实现合成
}
                                ⇣
SynthesizeClassEvent (D2c-多播 已 ship) →
    field Clicked: MulticastAction<int> = new MulticastAction<int>()
    method add_Clicked(...) { ... }
    method remove_Clicked(...) { ... }


TypeChecker.implementsInterface(Bus, IBus):
  signatures match → ✓ Bus 实现 IBus
```

## Decisions

### Decision 1: SynthesizeInterfaceEvent vs SynthesizeClassEvent 共享
**问题：** 两端合成 add/remove 共享代码？
**决定：不共享**。原因：
- 类端产 `FunctionDecl` (含 body)；interface 端产 `MethodSignature` (无 body)
- 字段 auto-init 仅 class 端需要
- 两个 helper 名字 + 签名清楚分开，参考 `SynthesizeClassAutoProp` vs `SynthesizeInterfaceAutoProp` 已有先例

### Decision 2: interface event 必须 instance abstract（无 default body）
**决定：** interface 内 event 仅声明 add/remove signature，不提供 default body。原因：
- z42 instance default interface methods 暂未支持（[TopLevelParser.Types.cs:198-201](src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs#L198-L201)）
- `static virtual` 默认 body 与 event 语义不符（event 是 instance state）
- 用户必须在 implementer class 写 `event ...` 触发 D2c-多播 合成

### Decision 3: 单播 event 在 interface 同样报错
**决定：** 与 class 端一致 —— `event Action<T> Y;` 在 interface 内也报 "single-cast event not yet supported"。Spec 2b 落地后双端解锁。

## Implementation Notes

- `SynthesizeInterfaceEvent(TypeExpr fieldType, string fieldName, Span span)`：
  - 验证类型 `GenericType("MulticastAction", [T])`
  - 返回 `List<MethodSignature>` 含 add_X 和 remove_X 各 1 个 instance abstract signature
- ParseInterfaceDecl 检测 `event` modifier（在 ParseNonVisibilityModifiers 之后）：
  - 解析 type + name + `;`（与 class field 同模式但不存 FieldDecl）
  - 调 SynthesizeInterfaceEvent，methods.AddRange(...)

## Testing Strategy

- 单元测试：验证 interface event 声明产生正确 MethodSignatures（add_X / remove_X）
- 单元测试：interface 单播 event 报 not-yet-supported
- Golden test `interface_event/source.z42`：端到端
  - 定义 IBus interface 含 event Clicked
  - class Bus 实现 IBus
  - `IBus b = new Bus(); b.Clicked += h; b.Clicked -= h;`
  - 验证 vtable dispatch 正确触发
- D2a / D2b / D-5 / D2c-多播 既有 golden 全 GREEN
