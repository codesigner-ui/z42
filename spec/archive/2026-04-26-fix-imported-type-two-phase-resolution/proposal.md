# Proposal: ImportedSymbolLoader 两阶段加载 — 消除类型降级

## Why

Wave 2 实施时发现 TypeChecker 限制：用户代码 `outer.InnerException = inner;`
（两个 `Std.Exception` 实例之间赋值）报 E0402 "cannot assign Exception to
Exception"。

**根因定位**：[ImportedSymbolLoader.ResolveTypeName:187](src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs#L187)

```csharp
// Unknown type name — treat as a class reference (will be resolved
// when the class is also imported, or remain as a named prim type).
_ => new Z42PrimType(name),
```

加载 stdlib TSIG 时，类的字段 / 方法签名里如果引用了"未知名"（既不是 prim
type，也不是当前模块的 generic param），就**降级为 `Z42PrimType(name)`**。

这是经典的 forward / self-reference 降级问题：
- Phase 1（`RebuildClassType` 解析自身字段时）`classes` 字典还没填到自己，
  `Exception.InnerException` 字段查不到 `Exception` ClassType，降级为 PrimType
- 注释说"will be resolved when the class is also imported, or remain as
  a named prim type" — **从未实现"resolved"那一步**

下游影响：
- `IsAssignableTo`（[Z42Type.cs:39-87](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs#L39-L87)）
  只匹配 `Z42ClassType vs Z42ClassType`（line 63），不识别 PrimType-bridge
- 用户代码侧的 `inner` 变量类型是 `Z42ClassType("Exception")`（来自 `new`），
  字段类型是 `Z42PrimType("Exception")`（来自 TSIG 降级）→ 同名但 kind 不同 → assign 失败

按 [.claude/rules/workflow.md "修复必须从根因出发"](.claude/rules/workflow.md#修复必须从根因出发-2026-04-26-强化)，
**禁止在 `IsAssignableTo` 加 `if (PrimType.Name == ClassType.Name) return true`
桥接补丁**。必须从源头消除降级状态。

## What Changes

参考 C# / Java 编译器的"两阶段类型加载"设计：

### Phase 1：建立骨架

遍历所有 `ExportedModule × ExportedClass / ExportedInterface`，为每个
类型创建"空骨架"：仅 Name + TypeParams + 占位的空 Fields / Methods 字典；
登记到 `classes` / `interfaces` 字典。

```csharp
// Pass 1: 仅创建骨架并登记
foreach (var mod in modules) {
    foreach (var cls in mod.Classes) {
        classes[cls.Name] = CreateEmptySkeleton(cls);
        classNs[cls.Name] = mod.Namespace;
    }
    foreach (var iface in mod.Interfaces) {
        interfaces[iface.Name] = CreateEmptySkeleton(iface);
    }
}
```

### Phase 2：填充成员

骨架字典完整后，遍历每个类型，解析其 Fields / Methods 签名。`ResolveTypeName`
现在能在 classes / interfaces 字典里找到对应骨架，不再降级为 PrimType：

```csharp
// Pass 2: 填充字段和方法
foreach (var mod in modules) {
    foreach (var cls in mod.Classes) {
        var skeleton = classes[cls.Name];
        FillMembers(skeleton, cls, classes, interfaces);
        // skeleton 通过 with-mutation 或 record-replacement 填充
    }
    // ... 同样处理 interfaces
}
```

### `ResolveTypeName` 升级

新增参数 `IReadOnlyDictionary<string, Z42ClassType>? classes` +
`IReadOnlyDictionary<string, Z42InterfaceType>? interfaces`：

```csharp
return name switch
{
    "int"    or "i32" => Z42Type.Int,
    // ... primitives ...
    _ when classes?.TryGetValue(name, out var ct)    is true => ct,
    _ when interfaces?.TryGetValue(name, out var it) is true => it,
    _ => new Z42PrimType(name),  // 真正未知（非 stdlib 已知）
};
```

Phase 2 调用时传入完整 classes / interfaces；`Z42PrimType` 仅作 Phase 1
内部占位（不出现在最终 ImportedSymbols 里）。

## Scope（允许改动的文件/模块）

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | refactor | 核心：拆分 Phase 1 / Phase 2；`ResolveTypeName` 加 classes/interfaces 参数 |
| `src/compiler/z42.Tests/TypeCheckerTests.cs` 或新建 | add | 单元测试：self-reference / forward-reference 类型解析 |
| `src/runtime/tests/golden/run/` | add | golden test：用户代码同类型字段 self-assign 编译 + 运行 |
| `docs/design/compiler-architecture.md` | edit | 在 "TSIG 与跨包符号导入" 章节补"两阶段加载"小节 |

## Out of Scope

- `Z42PrimType("X")` 中 X 是真正未知（非任何已加载 class/interface）的处理
  — 保留现有 fallback 行为，TypeChecker 后续若需要会报错
- 接口 property 支持（限制 #4，独立 change `add-auto-property-syntax`）
- ObjNew ctor 重载（限制 #2，独立 change `add-objnew-ctor-name`）
- IsAssignableTo 函数本身改动 — 修复后类型实际上就是 ClassType vs ClassType，
  现有 line 63 already handles it。**不加任何兼容分支**。

## Open Questions

- [x] Phase 1 的"骨架"是否要包含 BaseClass / Interfaces 链？—— 包含。
  `BaseClassName` 是 string，不需要 type 解析；ClassType 的 BaseClassName
  字段就是字符串，Phase 1 直接填即可。
- [x] Z42ClassType 是 record，如何"先建骨架后填充"？—— C# record 不可变，
  Phase 1 先存"参数"占位（用 Builder / Mutable wrapper），Phase 2 实际
  构造 final Z42ClassType。或用占位 record + dictionary key 替换为最终。
  Design.md 决策。
- [x] InterfaceType 的 BaseInterfaces 也涉及类型 reference — 同样两阶段处理。

## Blocks / Unblocks

- **Blocks**：Wave 2 限制 #3（self-reference assign）即此 change。修复后：
  - `outer.InnerException = inner;` 编译通过
  - Exception InnerException 链路对用户代码可用
  - 一般性 forward-reference / self-reference 模式（不限于 Exception）也修复
- **Unblocks**：未来任何"用户类字段引用同类型 / 跨包类型"的 stdlib 设计
- 不涉及 IR / VM / zbc 格式
