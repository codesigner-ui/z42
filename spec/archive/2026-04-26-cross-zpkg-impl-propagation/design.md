# Design: cross-zpkg `impl` 块传播

## Architecture

```
编译端 (z42.numerics → z42.numerics.zpkg)
─────────────────────────────────────────
SymbolCollector.Impls.cs
   merge impl 进 _classes[target].Methods       ← L3-Impl1，已有
        ↓
TypeChecker.cs:TryBindImplMethods                ← L3-Impl1，已有
        ↓
IrGen.cs:line 129-154
   for each cu.Impls:
     funcParams[QualifyClassName(target).method] ← FIX: was QualifyName
     emit method body as IrFunction              ← 已有，但 name fix 后符号正确
        ↓
ExportedTypeExtractor
   ExtractClasses (skip imported)                ← 已有
   〔NEW〕ExtractImpls(cu) → List<ExportedImplDef>
        ↓
ZbcWriter / ZpkgWriter
   write classes/interfaces/enums (TSIG)         ← 已有
   〔NEW〕write IMPL section
   write function bodies (MODS)                  ← 已有

消费端 (用户代码 using z42.core + z42.numerics)
─────────────────────────────────────────────
ZpkgReader
   parse TSIG → ExportedClass[] / ExportedInterface[]
   〔NEW〕parse IMPL → ExportedImplDef[]
        ↓
ImportedSymbolLoader.Load
   Phase 1: skeleton 登记                        ← 已有
   Phase 2: members 填充                          ← 已有
   〔NEW〕Phase 3: impl merge
     foreach impl in modules.SelectMany(m => m.Impls):
       targetClass = classes[impl.TargetFq]      ← 必须已存在
       foreach method in impl.Methods:
         targetClass.Methods.TryAdd(method.Name, method.Sig)  // first-wins
       classInterfaces[impl.TargetFq].Add(trait)             // 去重
        ↓
TypeChecker
   classes[Std.int].Methods 含 op_Add ✓
   classInterfaces[Std.int] 含 Std.INumber<int> ✓
   `where T: INumber<int>` 检查通过 ✓

运行时 (VM 加载 zpkg + 调用)
──────────────────────────
Lazy Loader
   load z42.numerics.zpkg → register MODS funcs into func_index
     func_index["Std.int.op_Add"] = numerics_op_add_body  ← Already works!
   skip IMPL section（VM 不消费）
        ↓
VCallInstr(int_obj, "op_Add", [other])
   → primitive_class_name(I64) = "Std.int"
   → func_name = "Std.int.op_Add"
   → func_index lookup → numerics body ✓
```

## Decisions

### Decision 1: IMPL section 还是嵌入 TSIG？

**问题**：IMPL records 放新独立 section，还是塞进现有 TSIG section（作为
ExportedClass 的 `ImplExtensions` 字段）？

**选项**：
- A：独立 IMPL section
- B：TSIG.ExportedClass 加字段

**决定**：**A — 独立 IMPL section**。

**理由**：
- TSIG.ExportedClass 是"我导出的本包类型定义"。impl 块描述"我对**外部
  类型**的扩展"，语义不属于"本包导出的类型"
- TSIG 跳过 imported classes（line 32），impl 的 target 大概率是 imported
  → 嵌进 ExportedClass 会与现有过滤逻辑冲突
- 独立 section 让 VM 可以选择性跳过，未来 deny-list / orphan-rule 检查也
  容易在加载阶段单独处理

### Decision 2: ImportedSymbolLoader Phase 3 vs Phase 2

**问题**：impl merge 应在 Phase 2 内做，还是新增 Phase 3？

**决定**：**Phase 3**（独立步骤）。

**理由**：
- Phase 2 in-place mutate 已经基于 Phase 1 的 skeleton dict references
- impl merge 也要 mutate 这些同一字典（target 是 Phase 1 已登记的 class）
- 但 impl merge 只能在所有 class 的 Methods 已经填好后做（否则同名冲突
  检查不准）
- 把 impl merge 放 Phase 2 末尾会让 Phase 2 逻辑不内聚（Phase 2 是"按
  ExportedModule 填本包导出"，不是"跨模块合并"）

### Decision 3: Method body 是否进 IMPL section？

**问题**：method body（IR 指令流）是否序列化到 IMPL section？

**决定**：**不**。method body 仍走 MODS section。

**理由**：
- z42.numerics 的 impl 方法 body 是 z42.numerics zpkg 的代码资产，与其他普通
  函数 body 同质。MODS section 已经能承载，无需重复
- 函数符号 `Std.int.op_Add` 让 VM `func_index` 在加载 z42.numerics 时
  自动注册，VM 端 zero-change（这是设计的核心简化）
- IMPL section 只承载**声明级元数据**（target/trait/方法签名），让 TypeChecker
  能 reconstruct，与其它 TSIG 风格一致

### Decision 4: 同名 method 冲突策略

**问题**：跨包 impl 把方法合并进 imported class 时，遇到冲突怎么办？

**决定**：**first-wins，silent skip**（与 ImportedSymbolLoader.MergeImported 既有
`TryAdd` 语义一致）。

**理由**：
- 跨包冲突大多是无意（两个扩展包都给 `int` 加了 `Stringify`）
- 报错会让用户必须修代码 / 重排 using —— 实施成本高
- first-wins 至少保证可编译；如果引发运行时 bug，用户可以观察行为重排
- 严格冲突检查留给后续"孤儿规则收紧"迭代

### Decision 5: target 必须是 imported 吗？

**问题**：如果 target 是本地 class，要不要也写 IMPL section？

**决定**：**也写**（无差异处理）。

**理由**：
- 设想：z42.linq 给 z42.collections 的 `IEnumerable<T>` 实现者（List / Stack）
  加 `Where` / `Select` 扩展。z42.linq 引用 z42.collections，target 是
  imported；但 z42.collections 也可能 self-extend，target 是 local
- 让 IMPL section schema 不区分 local/imported target，简化序列化
- 消费者 Phase 3 做合并时，target FQ 名匹配 `_classes` dict 即可，不区分来源
- 唯一限制：target FQ 名必须在 imported / local class 集合中存在 —— 否则报警告
  跳过（防止 stale impl）

### Decision 6: trait TypeArgs 序列化格式

**问题**：`impl INumber<int> for int` 中 trait 的 TypeArgs `<int>` 怎么写？

**决定**：复用 `ExportedMethodDef.Params` 的字符串编码（如 `"int"`、`"List<string>"`），
ExportedImplDef 含 `List<string> TraitTypeArgs`。

**理由**：
- 消费者 Phase 3 用现有 `ResolveTypeName` 解析字符串 → Z42Type
- 与 ExportedClassDef.Interfaces 的字符串列表风格一致
- 序列化简单，pool idx 引用即可

### Decision 7: IrGen.cs:132 fix scope

**问题**：把 `QualifyName` 改 `QualifyClassName` 是否影响 L3-Impl1 场景（同 CU
local target）？

**决定**：不影响。

**理由**：
- `QualifyClassName(local_class)` 检查 sem.Classes + !ImportedClassNames →
  返回 `QualifyName(local)` —— 与原行为一致
- 只在 target 是 imported 时改变行为：返回 imported 的命名空间
- 现有 golden test 86_extern_impl_user_class（target=local Robot）不变

### Decision 8: VM 是否需要解码 IMPL section？

**问题**：Rust VM decoder 是否需要把 IMPL section 解析成数据结构？

**决定**：**只需"能跳过"**。

**理由**：
- VM dispatch 完全不依赖 IMPL section（method body 已在 MODS，func_index
  自动注册）
- 但解码器必须**容忍** IMPL section 的存在（不能因为遇到未知 section 报错）
- 实施：decoder 读到 `IMPL` tag → seek skip 到 size 之后；不构造数据结构
- 简化：未来如果 VM 真要用 impl 元数据（运行时 trait 检查），再扩展

## Implementation Notes

### ExportedImplDef shape

```csharp
public sealed record ExportedImplDef(
    string TargetFqName,        // 例如 "Std.int"
    string TraitFqName,         // 例如 "Std.INumber"
    List<string> TraitTypeArgs, // 例如 ["int"]
    List<ExportedMethodDef> Methods);
```

### ExportedTypeExtractor.ExtractImpls

```csharp
private static List<ExportedImplDef> ExtractImpls(SemanticModel sem, CompilationUnit cu)
{
    var result = new List<ExportedImplDef>();
    foreach (var impl in cu.Impls)
    {
        if (impl.TargetType is not NamedType tnt) continue;
        if (impl.TraitType  is not NamedType ant
         && impl.TraitType  is not GenericType agt) continue;

        // 用 ImportedClassNamespaces 解析 target FQ 名（imported）或本包 FQ（local）
        string targetFq = sem.ImportedClassNamespaces.TryGetValue(tnt.Name, out var tns)
            ? $"{tns}.{tnt.Name}"
            : $"{cu.Namespace}.{tnt.Name}";

        var (traitName, traitArgs) = impl.TraitType switch
        {
            NamedType n   => (n.Name, new List<TypeExpr>()),
            GenericType g => (g.Name, g.TypeArgs.ToList()),
            _ => (null!, null!)
        };
        // 类似处理 trait FQ + TypeArgs 字符串化
        // ...
        result.Add(new ExportedImplDef(targetFq, traitFq, traitArgsStr, methods));
    }
    return result;
}
```

### ImportedSymbolLoader Phase 3

```csharp
private static void MergeImpls(
    Dictionary<string, Z42ClassType>            classes,
    Dictionary<string, Z42InterfaceType>        interfaces,
    Dictionary<string, List<Z42InterfaceType>>  classInterfaces,
    IReadOnlyList<ExportedImplDef>              impls,
    HashSet<string>                             genericTypeParams)
{
    foreach (var impl in impls)
    {
        // target 必须已在 classes 中（来自其它 zpkg 的 ExportedClass）
        if (!classes.TryGetValue(impl.TargetFqName, out var target))
            continue;  // 跳过 stale impl

        // resolve trait type args
        var traitArgs = impl.TraitTypeArgs
            .Select(s => ResolveTypeName(s, genericTypeParams, classes, interfaces))
            .ToList();

        // build trait interface ref
        if (!interfaces.TryGetValue(impl.TraitFqName, out var trait))
            continue;
        var traitInst = traitArgs.Count > 0
            ? new Z42InterfaceType(trait.Name, trait.Methods, traitArgs,
                                   trait.StaticMembers, trait.TypeParams)
            : trait;

        // merge methods (first-wins)
        var methodsDict = (Dictionary<string, Z42FuncType>)target.Methods;
        foreach (var m in impl.Methods)
        {
            var sig = BuildFuncTypeFromExported(m, genericTypeParams, classes, interfaces);
            methodsDict.TryAdd(m.Name, sig);
        }

        // append trait to classInterfaces
        if (!classInterfaces.TryGetValue(impl.TargetFqName, out var ifList))
            classInterfaces[impl.TargetFqName] = ifList = new List<Z42InterfaceType>();
        if (!ifList.Any(i => i.Name == traitInst.Name))
            ifList.Add(traitInst);
    }
}
```

### golden test 102_cross_zpkg_impl

由于 z42.numerics 包不存在，golden test 用临时 mock：
- 创建临时目录 `tmp/test-pkg-numerics/` with `z42.toml` + `src/Ext.z42`：
  ```z42
  using z42.core;
  // 给 z42.core 的某个 class 加新接口（避免 primitive 复杂度）
  // 例：给 List<int> 加 ISummable
  ```
- 编译 mock 包成 zpkg
- 用户代码 using 这个 zpkg
- 跑 VM 验证 trait 调用通过

由于临时 mock 包搭建复杂，**实际 golden test 简化为单一 zpkg 自给**：
两个 source files 在同一 z42.toml lib 下，但**模拟跨包**通过强制 ImportedSymbolLoader
路径（编译完毕后清空 zpkg cache 仅留对应 zpkg，再编译消费者代码）。

具体方案在 tasks.md 阶段 4 细化。

## Testing Strategy

### 单元测试 (z42.Tests)

- IMPL section roundtrip：序列化一个 ExportedImplDef list → 反序列化 → 内容相等
- ImportedSymbolLoader Phase 3 merge：构造 ImportedSymbols + ImplDefs，验证 target
  Methods 含 impl 方法，ClassInterfaces 含 trait
- 冲突处理：first-wins、target 不存在时 skip

### Golden test

- `run/102_cross_zpkg_impl/`：模拟 z42.numerics-style 扩展包给 z42.core 的某个
  类型加 trait + 方法，调用通过

### 端到端

- 清空 zpkg cache，rebuild stdlib 5/5
- dotnet test 全绿
- ./scripts/test-vm.sh 全绿
- ./scripts/test-dist.sh 全绿

## 兼容性

| 风险 | 评估 | 缓解 |
|------|------|------|
| zbc 0.7 → 0.8 不兼容 | 必然 | 按 pre-1.0 规则直接 bump；regen-golden-tests 重生 |
| VM decoder 不识别 IMPL section | 中 | decoder 加 IMPL tag handling（skip） |
| 现有 stdlib 没有 impl 块 → IMPL section 总是空 | 低 | 写空 list（0 records）—— 5 字节开销可忽略 |
| 跨包同名冲突 | 中 | first-wins 默认；后续可加 verbose warning |
