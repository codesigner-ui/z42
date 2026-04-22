# Proposal: L3-G4d stdlib 泛型类导出

## Why

L3-G4c 已验证用户层能完整写 `class MyList<T>` 源码容器。但把参考实现移到 stdlib
（`Std.Collections.Stack<T>` 等）并让 user 代码直接用，失败于多个配套缺口：

1. **TSIG 不携带 TypeParams**：`ExportedClassDef` 没有 `TypeParams` 字段，TSIG 读回后 `Std.Collections.Stack` 不是泛型类，`new Stack<int>()` 会报类型参数个数不匹配
2. **名称冲突**：SymbolCollector 对 imported + local 同名类报 `duplicate class declaration`（撞 `38_self_ref_class` 用户自定义 Stack 和 stdlib Stack）
3. **IrGen 路由**：`new Stack<int>()` 没走 `QualifyClassName`，ObjNew instr 用本地命名空间而非 `Std.Collections.Stack`
4. **VM 分派**：stdlib 类加载时 TypeDesc 注册为 `Std.Collections.Stack`，但 user IR 里 class_name 是 `Stack` → 注册表查不到；VCall 回退到短名找不到方法

这是 L3-G4 收尾（pseudo-class List/Dict 真正替换）的关键前置。打通后 stdlib 容器
真正可作泛型类导出；L3-G4e 索引器 / L3-G4f List-Dict 替换会站在这个基础上。

## What Changes

### TSIG 格式扩展（zpkg 版本不变，格式兼容：插入位置在 flags 之后）

`ExportedClassDef` 结构：
```
name_idx: u32          (already)
base_class_idx: u32    (already; u32::MAX = none)
flags: u8              (already; abstract/sealed/static)
+ tp_count: u8         NEW
+ tp_name_idx[]: u32 × tp_count   NEW
field_count: u16       (already)
...
```

ZpkgWriter / ZpkgReader 同步。zpkg 版本 **不 bump**（version 已在 L3-G3a 升到 0.5）,
仅 TSIG 布局微调。stdlib 全量重编覆盖。

### 编译器

- `ExportedClassDef`：加 `TypeParams: List<string>?`
- `ExportedTypeExtractor.Extract`：读取 `Z42ClassType.TypeParams` 填充到 exported
- `ImportedSymbolLoader.RebuildClassType`：保留 TypeParams 到重建的 `Z42ClassType`
- `SymbolCollector.Classes.cs`：**local 同名覆盖 imported**：
  - 若 `_classes` 已含 cls.Name 且该条目是 imported（`_importedClassNames.Contains`），移除 imported 记录，继续注册 local
  - 否则保留原来的 duplicate 检查
  - 同时从 `_importedClassNamespaces` 移除被覆盖的条目
- `IrGen.QualifyClassName`：**local 优先**：若 `SemanticModel.Classes` 里有该名且不在 `ImportedClassNames` 里 → 用 `QualifyName`；否则若在 `ImportedClassNamespaces` → 用 `{ns}.{name}`；否则 `QualifyName`
- `FunctionEmitterExprs.EmitBoundNew`：default 分支用 `QualifyClassName` 而非 `QualifyName`
- `FunctionEmitterCalls`：方法调用的 class qualification 同步用 `QualifyClassName`

### Rust VM

- 新增 **qualified-name 查找**：ObjNew 和 VCall 在 type_registry 找不到时，尝试 lazy_loader
- stdlib 类 TypeDesc 用完整命名空间名注册（既有行为）
- VCall 对 imported class receiver 使用 `type_desc.name`（已经是 qualified）composer 函数名 — 已支持

### stdlib

- `src/libraries/z42.collections/src/Stack.z42`：取消注释启用 `Std.Collections.Stack<T>`
- `src/libraries/z42.collections/src/Queue.z42`：同
- 不动 `List.z42` / `Dictionary.z42`（继续 pseudo-class，L3-G4f 再替换）

### 测试

- Golden `run/77_stdlib_stack_queue`：user 代码 `new Stack<int>()` / `new Queue<string>()` 端到端
- Golden `run/78_stdlib_generic_shadow`：user `class Stack { }` 覆盖 stdlib Stack，走 local 实现
- ZbcRoundTripTests：TSIG TypeParams round-trip
- 现有 38_self_ref_class（user Stack）、74_generic_stack（user Stack）保持通过

## Scope

| 文件/模块 | 变更 |
|-----------|------|
| `z42.IR/ExportedTypes.cs` | `ExportedClassDef` 加 `TypeParams: List<string>?` |
| `z42.Project/ZpkgWriter.cs` | TSIG section 写 tp_count + names |
| `z42.Project/ZpkgReader.cs` | TSIG section 读 tp_count + names |
| `z42.Semantics/TypeCheck/ExportedTypeExtractor.cs` | 填充 TypeParams |
| `z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | 保留 TypeParams |
| `z42.Semantics/TypeCheck/SymbolCollector.Classes.cs` | local 覆盖 imported 同名 |
| `z42.Semantics/Codegen/IrGen.cs` | `QualifyClassName` local 优先 |
| `z42.Semantics/Codegen/FunctionEmitterExprs.cs` | EmitBoundNew 用 QualifyClassName |
| `z42.Semantics/Codegen/FunctionEmitterCalls.cs` | 方法调用 class qualification |
| `src/runtime/src/interp/exec_instr.rs` | ObjNew 查 type_registry 未命中时查 lazy_loader |
| `src/libraries/z42.collections/src/Stack.z42` | 启用真实 `Stack<T>` |
| `src/libraries/z42.collections/src/Queue.z42` | 启用真实 `Queue<T>` |
| `src/compiler/z42.Tests/ZbcRoundTripTests.cs` | TSIG TypeParams round-trip |
| `src/runtime/tests/golden/run/77_stdlib_stack_queue/` | 新 golden |
| `src/runtime/tests/golden/run/78_stdlib_generic_shadow/` | 新 golden |
| `docs/design/generics.md` | L3-G4d 小节 |
| `docs/roadmap.md` | L3-G4d → ✅ |

## Out of Scope

- `List<T>` / `Dictionary<K,V>` pseudo-class 替换（L3-G4f，需 L3-G4e 索引器）
- 索引器语法 `T this[int]`（L3-G4e）
- 泛型 interface 跨模块导出的完整处理（留给 L3-G3d TSIG 约束扩展）
- TSIG 携带约束（L3-G3d）
- `using Std.Collections;` 短名导入（语言特性，独立迭代）

## Open Questions

- [ ] 若 user 代码写 `using Std.Collections;` + 本地无 Stack → 行为？**决策**：z42 当前没有 `using` 导入，用户用短名 `Stack` 默认指向唯一可见的（local 或 imported）；若两者都缺 / 冲突，报错
- [ ] stdlib `Stack<T>` 里有 bug 怎么回滚？**决策**：stdlib 源文件开头加 `// z42.generics: L3-G4d` 标签，问题定位后可单独还原注释版本
- [ ] 是否需要破坏性 zpkg 版本 bump？**决策**：不需要。TSIG 新字段是可选追加（tp_count=0 时 0 字节）；现有代码读 0 即安全。但为安全起见 reader 检查 tp_count 是否存在于 section 剩余空间
