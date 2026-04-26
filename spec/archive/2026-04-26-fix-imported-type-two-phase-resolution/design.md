# Design: ImportedSymbolLoader 两阶段加载

## Architecture

```
Load(modules, usings) 旧:                Load(modules, usings) 新:
─────────────────────────                 ─────────────────────────
foreach mod:                              ── Phase 1: 骨架登记 ──
  foreach cls:                            foreach mod:
    classes[cls.Name] = RebuildClassType(   foreach cls:
      cls)                                    classes[cls.Name] =
        ↓ (RebuildClassType)                    BuildSkeleton(cls)
        foreach field:                          (空 fields/methods 字典 + Name + TypeParams)
          ResolveTypeName(field.TypeName)   foreach iface:
            ↓ Z42PrimType (forward-ref         interfaces[iface.Name] = ...
            或 self-ref 时降级)
                                          ── Phase 2: 填充成员 ──
                                          foreach mod:
                                            foreach cls:
                                              FillMembers(classes[cls.Name],
                                                cls, classes, interfaces)
                                              ↓ ResolveTypeName(field.TypeName,
                                                  classes, interfaces)
                                                ↓ classes[name] / interfaces[name]
                                                  → 命中 (no downgrade)
```

## Decisions

### Decision 1: Z42ClassType record 的"先骨架后填充"实现方式

**问题**：`Z42ClassType` 是 C# `sealed record`，不可变；如何"先建空骨架，再填充"？

**选项：**
- **A**：把 ClassType 改为可变 class — 影响面太大（影响所有 TypeChecker 使用）
- **B**：Phase 1 创建"占位 record"（空字段/方法字典），Phase 2 创建"最终
  record"，**替换 dict 的 value**（同 key 引用）
- **C**：Phase 1 用 Builder 类（可变），Phase 2 调 `.ToRecord()` 一次性输出

**决定**：**B —— 替换字典 value**。

```csharp
// Phase 1
foreach (var cls in mod.Classes) {
    classes[cls.Name] = new Z42ClassType(
        cls.Name,
        Fields: new Dictionary<string, Z42Type>(),  // 空，待填
        Methods: new Dictionary<string, Z42FuncType>(),
        StaticFields: new Dictionary<string, Z42Type>(),
        StaticMethods: new Dictionary<string, Z42FuncType>(),
        MemberVisibility: new Dictionary<string, Visibility>(),
        BaseClassName: cls.BaseClass,
        TypeParams: cls.TypeParams?.AsReadOnly(),
        IsStruct: false
    );
}

// Phase 2
foreach (var cls in mod.Classes) {
    var skeleton = classes[cls.Name];
    var filled = RebuildClassTypeWithMembers(cls, classes, interfaces, skeleton.TypeParams);
    classes[cls.Name] = filled;  // 替换 dict value
}
```

**理由**：
- 不需要改 `Z42ClassType` record 定义，影响面最小
- ImportedSymbols 最终输出的 dict 里都是 final 实例
- 字典 key 不变，下游 lookup 不受影响
- 注意：**Phase 1 的"骨架对象"在 Phase 2 之前不应被泄露给 TypeChecker**，
  否则会有"半成品"风险。本变更里 Load 是同步调用，Phase 1 / Phase 2 在同
  函数内连续完成，无泄露窗口。

### Decision 2: Z42PrimType 仅作 Phase 1 内部占位

**问题**：Phase 1 时已经在 dict 里加了骨架，Phase 1 内部其他类型（基类引用 /
继承的 interface 列表）解析能否完全避免 PrimType 降级？

**决定**：Phase 1 **只**填 ClassType / InterfaceType 的 `Name` 和 `TypeParams`
和 `BaseClassName`（字符串，不需要类型解析），其他成员一律 Phase 2 处理。
所以 Phase 1 不调用 `ResolveTypeName`。Phase 2 调用时已有完整字典，绝不降级。

**理由**：清晰分阶段，Phase 1 的输入只是字符串，Phase 2 的输入是完整字典 +
字符串。逻辑无重叠。

### Decision 3: ResolveTypeName 新参数默认值

**问题**：扩展 `ResolveTypeName` 加 classes / interfaces 参数后，旧调用点
（Phase 1 的 `BuildSkeleton`，以及测试代码可能直接调）如何兼容？

**决定**：参数 nullable 默认 null。当 null 时按旧路径走（Phase 1 行为，
未知名→PrimType）。Phase 2 必须传非 null 字典。

```csharp
internal static Z42Type ResolveTypeName(
    string name,
    HashSet<string>? genericParams = null,
    IReadOnlyDictionary<string, Z42ClassType>? classes = null,
    IReadOnlyDictionary<string, Z42InterfaceType>? interfaces = null)
{
    // ... primitives + generic params ...

    if (classes?.TryGetValue(name, out var ct)    is true) return ct;
    if (interfaces?.TryGetValue(name, out var it) is true) return it;

    return new Z42PrimType(name);  // 真正未知 / Phase 1 占位
}
```

**理由**：API 向后兼容；调用点显式选择是否传字典；类型语义通过参数有无来区分阶段。

### Decision 4: Interface 同样两阶段

**问题**：`RebuildInterfaceType` 也调 `ResolveTypeName`，是否也需两阶段？

**决定**：是。Phase 1 创建空 InterfaceType 骨架（仅 Name + TypeParams +
StaticMembers 占位），Phase 2 填充 Methods / StaticMembers 实际签名。

**理由**：对称性；`IEnumerable<T>` 引用 `IEnumerator<T>` 是接口间 forward
reference 的现成例子。

### Decision 5: 不动 Z42Type.IsAssignableTo

**决定**：**严格不改 `IsAssignableTo`**。spec 已写入 Requirement"不引入兼容分支"。
PR review 时 reviewer 检查 `git diff Z42Type.cs` 应为空。

**理由**：根因在数据源（ImportedSymbolLoader），不在比较函数。`IsAssignableTo`
本身已正确处理 `Z42ClassType vs Z42ClassType same-name` 情况（Z42Type.cs:63）。
修源后 IsAssignableTo 输入即正确，无需任何改动。

### Decision 6: Generic params 的处理

**问题**：`tpSet`（HashSet<string> 当前类的类型参数）和新增 classes /
interfaces 参数如何配合？

**决定**：保留 `tpSet`，优先级高于 classes / interfaces：

```
1. 是 prim type 名 → 对应 Z42Type
2. 在 tpSet 里 → Z42GenericParamType
3. 在 classes 里 → Z42ClassType
4. 在 interfaces 里 → Z42InterfaceType
5. 否则 → Z42PrimType (Phase 1 占位 或 真未知)
```

`T` / `K` / `V` 等类型参数总是先于跨类查找匹配。

### Decision 7: classConstraints / classInterfaces 的处理

ImportedSymbolLoader 还产出 `classConstraints` / `classInterfaces`（L3-G3d
泛型约束 + 接口列表），这些是 string list，不涉及类型对象解析。Phase 1 / 2
不影响这些字段。

## Implementation Notes

### 重构后 Load 函数骨架

```csharp
public static ImportedSymbols Load(
    IReadOnlyList<ExportedModule> modules, IReadOnlyList<string> usings)
{
    var allowedNs = new HashSet<string>(usings, StringComparer.Ordinal);
    var classes    = new Dictionary<string, Z42ClassType>(StringComparer.Ordinal);
    var interfaces = new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal);
    // ... other dicts ...

    // ── Phase 1: 骨架登记 ──
    foreach (var mod in modules)
    {
        if (!allowedNs.Contains(mod.Namespace)) continue;
        foreach (var cls in mod.Classes)
            if (!classes.ContainsKey(cls.Name))
                classes[cls.Name] = BuildClassSkeleton(cls);
        foreach (var iface in mod.Interfaces)
            if (!interfaces.ContainsKey(iface.Name))
                interfaces[iface.Name] = BuildInterfaceSkeleton(iface);
    }

    // ── Phase 2: 填充成员 ──
    foreach (var mod in modules)
    {
        if (!allowedNs.Contains(mod.Namespace)) continue;
        foreach (var cls in mod.Classes)
            classes[cls.Name] = FillClassMembers(cls, classes, interfaces, classNs, mod.Namespace,
                                                  classConstraints, classInterfaces);
        foreach (var iface in mod.Interfaces)
            interfaces[iface.Name] = FillInterfaceMembers(iface, classes, interfaces);
        // ... enums / functions （这些不引用其他类，无需两阶段）
    }

    return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes,
                                classNs, classConstraints, funcConstraints, classInterfaces);
}

private static Z42ClassType BuildClassSkeleton(ExportedClassDef cls)
{
    var typeParams = cls.TypeParams is { Count: > 0 } tps ? tps.AsReadOnly() : null;
    return new Z42ClassType(
        cls.Name,
        Fields: new Dictionary<string, Z42Type>(),
        Methods: new Dictionary<string, Z42FuncType>(),
        StaticFields: new Dictionary<string, Z42Type>(),
        StaticMethods: new Dictionary<string, Z42FuncType>(),
        MemberVisibility: new Dictionary<string, Visibility>(),
        BaseClassName: cls.BaseClass,
        TypeParams: typeParams,
        IsStruct: false);
}

private static Z42ClassType FillClassMembers(
    ExportedClassDef cls,
    IReadOnlyDictionary<string, Z42ClassType>     classes,
    IReadOnlyDictionary<string, Z42InterfaceType> interfaces,
    Dictionary<string, string>                    classNs,
    string                                        modNs,
    Dictionary<string, List<ExportedTypeParamConstraint>> classConstraints,
    Dictionary<string, List<string>>              classInterfaces)
{
    var tpSet = cls.TypeParams is { Count: > 0 } tps
        ? new HashSet<string>(tps) : null;

    var fields        = new Dictionary<string, Z42Type>();
    var methods       = new Dictionary<string, Z42FuncType>();
    var staticFields  = new Dictionary<string, Z42Type>();
    var staticMethods = new Dictionary<string, Z42FuncType>();
    var memberVis     = new Dictionary<string, Visibility>();

    foreach (var f in cls.Fields)
    {
        var ft = ResolveTypeName(f.TypeName, tpSet, classes, interfaces);
        if (f.IsStatic) staticFields[f.Name] = ft;
        else            fields[f.Name]        = ft;
        memberVis[f.Name] = ParseVisibility(f.Visibility);
    }
    foreach (var m in cls.Methods)
    {
        var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet, classes, interfaces);
        if (m.IsStatic) staticMethods[m.Name] = sig;
        else            methods[m.Name]        = sig;
        var visKey = m.Name.Contains('$') ? m.Name[..m.Name.IndexOf('$')] : m.Name;
        memberVis.TryAdd(visKey, ParseVisibility(m.Visibility));
    }

    classNs[cls.Name] = modNs;
    if (cls.TypeParamConstraints is { Count: > 0 } cc)
        classConstraints[cls.Name] = cc;
    if (cls.Interfaces.Count > 0)
        classInterfaces[cls.Name] = new List<string>(cls.Interfaces);

    return new Z42ClassType(
        cls.Name, fields, methods, staticFields, staticMethods, memberVis,
        cls.BaseClass,
        cls.TypeParams is { Count: > 0 } tps2 ? tps2.AsReadOnly() : null,
        IsStruct: false);
}
```

`RebuildFuncType` 也增加 classes / interfaces 参数，传递给 `ResolveTypeName`。

### 测试设计

**单元测试**（`TypeCheckerTests` 或新建 `ImportedSymbolLoaderTests`）：

1. `Load_SelfReference_ProducesClassTypeNotPrimType` —
   `class A { A f; }` 加载后 A.Fields["f"] is Z42ClassType
2. `Load_ForwardReference_ProducesClassTypeNotPrimType` —
   两个 module，A 在 B 前导入，A.Fields["b"] 为 Z42ClassType("B")
3. `Load_TrueUnknownType_StillPrimType` — 拼写错误名仍为 Z42PrimType
4. `Load_InterfaceForwardReference` — IEnumerable<T> 引用 IEnumerator<T>
5. `Load_GenericParamPriority` — 同名 generic param 优先于 class lookup

**Golden tests** (`run/`)：

- `run/95_class_self_reference_field` — Exception 风格 self-reference 字段
  写读 + assign 同类型实例
- `run/96_class_forward_reference` — 跨模块 forward reference

### 兼容性

- 旧调用站点 `ResolveTypeName(name, tpSet)` 行为不变（PrimType for unknown）
- 不影响其他 TypeChecker / IR / VM 行为 — 修复仅改善 ImportedSymbols 的
  输入质量

## Testing Strategy

- 单元测试覆盖 Decision 1/4/6/7 各分支（5 个 case）
- Golden test 验证用户代码 self-reference assign 编译 + 运行
- 全量回归：dotnet test / test-vm.sh / cargo test 全绿
- **关键 review 检查**：`git diff Z42Type.cs` 应为空（Decision 5）

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| Phase 1 骨架的"半成品"被 TypeChecker 提前消费 | 低 | Load 同步调用；Phase 1/2 在同函数内连续完成 |
| `RebuildFuncType` 调用点遗漏新参数 | 中 | 找 grep 检查所有调用点；测试覆盖 |
| 单元测试现在拿到 ClassType 而以前是 PrimType，断言变化 | 中 | 修改测试断言；这是预期改进 |
| `classes` / `interfaces` 字典 mutation 期间被读 | 低 | Phase 2 内逐项替换，但读路径只查 `Phase 1 完整` 字典 |
