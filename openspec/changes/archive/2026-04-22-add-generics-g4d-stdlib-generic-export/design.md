# Design: L3-G4d stdlib 泛型类导出

## Architecture

```
 stdlib src              zpkg TSIG                  User compile            VM runtime
 ─────────────────────────────────────────────────────────────────────────────────────
 class Std.Coll         ExportedClassDef           ImportedSymbols         Module
 .Stack<T>              { name:"Stack",            classes["Stack"]        type_registry
 { ... }                  TypeParams:["T"], ... }  with TypeParams         ["Std.Coll.Stack"]
                                                    classNs["Stack"]
                                                      ="Std.Coll"
 
                          New user code:
                          new Stack<int>()        → ObjNew class_name="Std.Coll.Stack"
                                                    (QualifyClassName resolves
                                                     imported → qualified)
```

## Decisions

### Decision 1: TSIG 格式兼容性

**问题**：加 TypeParams 到 ExportedClassDef，如何处理旧 zpkg？

**选项**：
- A. zpkg 版本 bump，严格校验
- B. 可选追加：tp_count=0 时零字节，reader 按剩余 section 空间判断
- C. 单独新 section（TGEN）存泛型信息

**决定**：B 可选追加，但仓库内所有 zpkg 一次性重生成（与历次格式变更同）。
- tp_count 写为 u8 `0` 时仅占 1 字节，老 zpkg 缺失就用 0 作默认
- 但稳妥起见 **reader 检查 section 剩余空间**，不足则按 0 处理
- 仓库使用场景里 zpkg 都是 build 时生成，无真实"老 zpkg"兼容压力

### Decision 2: local 覆盖 imported 的裁决位置

**问题**：在哪一层做覆盖？

**选项**：
- A. TypeChecker 侧：SymbolCollector 注册 local 时移除 imported 同名
- B. IrGen 侧：QualifyClassName 查 local 优先
- C. 两侧都做

**决定**：C。
- A 避免 TypeChecker 在 body 绑定阶段拿到 imported 类（若被 local 覆盖）
- B 确保 ObjNew / 方法调用生成正确的 class_name
- 缺一不可：只做 A，Local Stack 只有 IR 发射路径可能还写了全限定名；只做 B，TypeChecker 在 body 内看到的 class 是 imported 的（方法集合不同）导致绑定错

### Decision 3: "local 优先"的判定

local 条件：`_classes.ContainsKey(name)` AND `!_importedClassNames.Contains(name)`（即 classes dict 里存在，且不是从 import 来的）。

Imported 覆盖时机（SymbolCollector.Classes.cs）：user 类注册时发现 `_classes` 已有同名且在 `_importedClassNames` → 移除 imported 记录，继续注册 local：

```csharp
if (_classes.ContainsKey(cls.Name))
{
    if (_importedClassNames.Contains(cls.Name))
    {
        _importedClassNames.Remove(cls.Name);
        _importedClassNamespaces.Remove(cls.Name);
        // fall through to register local; overwrite _classes entry
    }
    else
    {
        _diags.Error(..., "duplicate class declaration ...");
        continue;
    }
}
_classes[cls.Name] = new Z42ClassType(...);  // local
```

### Decision 4: qualified new 语法超范围

用户无法写 `new Std.Collections.Stack<int>()` — z42 parser 不识别这种带点的 type name。
本次不加这个语法；user 若要用 stdlib Stack 而 local 也定义了 Stack，就得重命名 local。
这个边界在 spec 的 Out of Scope 里明确。

### Decision 5: VM ObjNew lazy-load 触发

imported 类在 VM 侧可能还没加载（lazy loader）。ObjNew 查 type_registry 未命中时：

- 当前逻辑：fallback 到 `make_fallback_type_desc`（仅按 ClassDesc 构造空白 TypeDesc）
- 新增逻辑：先尝试 `lazy_loader::try_lookup_type(class_name)`；命中则用其 TypeDesc；否则 fallback

这保证 user 代码首次 `new Std.Collections.Stack<int>()` 时 stdlib 类能被正确加载。

### Decision 6: stdlib Stack<T> / Queue<T> 启用范围

只启用 Stack 和 Queue（新名字，不冲突 pseudo-class）。不动 `Std.Collections.List`、
`Std.Collections.Dictionary`（它们和 pseudo-class 冲突，留给 L3-G4f 一起替换）。

## Implementation Notes

### ExportedClassDef 扩展

```csharp
public sealed record ExportedClassDef(
    string Name,
    string? BaseClass,
    bool IsAbstract,
    bool IsSealed,
    bool IsStatic,
    List<ExportedFieldDef>  Fields,
    List<ExportedMethodDef> Methods,
    List<string> Interfaces,
    List<string>? TypeParams = null);  // NEW
```

### ZpkgWriter TSIG class encoding

当前布局（简化）：
```
name_idx u32, base_raw u32, flags u8, field_count u16, [fields], method_count u16, [methods], iface_count u16, [ifaces]
```

新布局：
```
name_idx u32, base_raw u32, flags u8,
tp_count u8, tp_name_idx u32 × tp_count,          // NEW
field_count u16, [fields], method_count u16, [methods], iface_count u16, [ifaces]
```

### ZpkgReader TSIG class decoding

对称读取 tp_count 和 tp_name_idx[]。

### SymbolCollector 裁决代码（见 Decision 3）

`ImportedSymbolLoader.RebuildClassType`：`TypeParams: cls.TypeParams?.AsReadOnly()`。

### IrGen.QualifyClassName

```csharp
string IEmitterContext.QualifyClassName(string className)
{
    var sem = _semanticModel;
    if (sem is null) return QualifyName(className);
    // Local wins: classes dict has it and it's NOT in imported names
    if (sem.Classes.ContainsKey(className) && !sem.ImportedClassNames.Contains(className))
        return QualifyName(className);
    // Imported: use its original namespace
    if (sem.ImportedClassNamespaces.TryGetValue(className, out var ns))
        return $"{ns}.{className}";
    return QualifyName(className);
}
```

`SemanticModel` 需要暴露 `ImportedClassNames: IReadOnlySet<string>`（目前仅通过 Symbols 能间接获取；加一个直通字段）。

### FunctionEmitterExprs.EmitBoundNew

default 分支从 `QualifyName(n.QualName)` 改为 `_ctx.QualifyClassName(n.QualName)`。ctor 名仍 `{qualified}.{short}`。

### VM ObjNew lazy-load 尝试

```rust
// exec_instr.rs, Instruction::ObjNew
let type_desc = module.type_registry.get(class_name).cloned()
    .or_else(|| crate::metadata::lazy_loader::try_lookup_type(class_name))
    .unwrap_or_else(|| Arc::new(make_fallback_type_desc(module, class_name)));
```

VCall 路径已支持 `type_desc.name` 作前缀查找 (`{type_desc.name}.{method}`)；导入类走 lazy_loader 会命中目标函数。

### stdlib Stack.z42 / Queue.z42 启用

取消注释前次的真实实现。

## Testing Strategy

### C# 单元测试
- `ZbcRoundTripTests.cs`: 新增 `Tsig_ClassTypeParams_RoundTrip`
- `TypeCheckerTests.cs`: 新增"local 覆盖 imported 同名"用例（可能需 mock stdlib，或通过 integration 走 golden）

### Golden 运行测试
- `run/77_stdlib_stack_queue` — user `new Stack<int>()` / `new Queue<string>()` end-to-end
- `run/78_stdlib_generic_shadow` — user 自定义 Stack 覆盖 stdlib，走本地实现
- 既有 `run/38_self_ref_class`、`run/74_generic_stack` 保持通过

### 验证门
- `dotnet build` + `cargo build` 无错误
- `dotnet test` 全绿（520 + 至少 2 新 = 522+）
- `./scripts/build-stdlib.sh` 5/5 成功
- `./scripts/regen-golden-tests.sh` 全成功
- `./scripts/test-vm.sh` 全绿（146 + 4 新 = 150+）

## Risks

| 风险 | 缓解 |
|------|------|
| TSIG 格式改动破坏 ZpkgReader 老行为 | 新字段紧跟 flags 后，使用 u8 tp_count=0 零字节；读侧检查 remaining space，不足则按 0 处理 |
| Local 覆盖引入未预期行为（L1/L2 既有测试用 imported 类） | 38、74 直接跑验证；imported class 只覆盖在同名 local 时，不影响其他类 |
| stdlib Stack<T> 在 imported 路径有隐藏 bug（方法 ctor 不匹配） | 简单实现，仅 Push/Pop/Peek/Count；与 74_generic_stack 用户版本同构 |
| VM lazy-load 和 type_registry 重复注册冲突 | try_lookup_type 仅在 registry miss 时调用；lazy loader 内部缓存 |
| user 想用 Stack 作本地但 stdlib 也有 → 语义冲突 | local 覆盖是默认；若 user 要用 stdlib Stack，重命名 local 或用 using 导入（L3 后期特性） |
