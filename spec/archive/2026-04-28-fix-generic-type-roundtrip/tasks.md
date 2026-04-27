# Tasks: fix-generic-type-roundtrip

> 状态：🟢 已完成 | 类型：fix (typecheck/import/codegen/pipeline) | 创建：2026-04-28 | 完成：2026-04-28

**变更说明：** 修复跨 zpkg / 包内多 namespace 场景下 `KeyValuePair<K,V>` 等 instantiated generic 类信息丢失的 bug — 用户访问 `dict.Entries()[i].Value` 拿到 `null`（而非 `int`）。

**根因 4 处（按 pipeline 顺序）：**

1. **SymbolCollector.ResolveType (GenericType)** — 用户类的泛型实例化形式 `KeyValuePair<K, V>` 在 SymbolCollector 阶段被退化成 bare `Z42ClassType("KeyValuePair")`，丢失 type-args；下游签名收集 / TSIG 序列化全部跟着错
2. **ExportedTypeExtractor.TypeToString** — `Z42InstantiatedType` 落到 `_ => "unknown"`，TSIG 字符串里看到 `KeyValuePair[]`
3. **ImportedSymbolLoader.ResolveTypeName** — 不解析 `Foo<X, Y>` 语法，把字符串当 unknown class name → `Z42PrimType("KeyValuePair<K, V>")`
4. **PackageCompiler.ExtractIntraSymbols** — 同包多 CU 各自带不同 namespace（z42.core 同时含 `Std` / `Std.Collections` / `Std.IO`），原代码把所有 class 标成首个 CU 的 namespace；导致 Dictionary.Entries() 内 `new KeyValuePair<K, V>(...)` 被发射成 `Std.KeyValuePair`（错），运行期找不到类型，构造器不写字段，`.Value` 全是 null

**触发场景：** `Dictionary<string, int>.Entries()[i].Value`
- 修复前：返回类型在 zpkg round-trip 后变 `unknown[]` 或 namespace 错位 → typecheck 报 "got V" / runtime `.Value = null`
- 修复后：返回 `KeyValuePair<string, int>[]`，`.Value` 正确推断为 `int`，runtime 取到具体值

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | MODIFY | GenericType 分支保留 TypeArgs，构造 Z42InstantiatedType |
| `src/compiler/z42.Semantics/TypeCheck/ExportedTypeExtractor.cs` | MODIFY | TypeToString 加 Z42InstantiatedType 分支 |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | MODIFY | ResolveTypeName 加 `<...>` 解析；FindGenericOpenLt / SplitGenericArgs helpers |
| `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` | MODIFY | ExtractIntraSymbols 接受 per-class namespace map |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` | MODIFY | TypeName 加 GenericType 分支保留 type-args |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | TypeName 加 GenericType 分支（与 FunctionEmitter 同步） |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | MODIFY | 构建 per-CU namespace map 并传给 ExtractIntraSymbols |
| `src/runtime/tests/golden/run/20_dict_iter/source.z42` | MODIFY | 加 `entries[m].Value` 累加断言 |
| `src/runtime/tests/golden/run/20_dict_iter/expected_output.txt` | MODIFY | 加 `sum3=6` 期望行 |

## Tasks

- [x] 1.1 SymbolCollector.ResolveType — GenericType 分支构造 Z42InstantiatedType
- [x] 2.1 ExportedTypeExtractor.TypeToString — 加 Z42InstantiatedType 分支
- [x] 2.2 ImportedSymbolLoader.ResolveTypeName — 加 `<...>` 解析 + helpers
- [x] 2.3 FunctionEmitter.TypeName / IrGen.TypeName — 加 GenericType 分支
- [x] 3.1 SymbolTable.ExtractIntraSymbols — 接受 per-class namespace map
- [x] 3.2 PackageCompiler — 构建 per-CU namespace map 并传入
- [x] 4.1 20_dict_iter golden test — 加 `entries[m].Value` 累加断言
- [x] 5.1 build-stdlib + regen + dotnet test (725) + test-vm (200) 全绿
- [ ] 5.2 commit + push + 归档

## 备注

- 原始 spec 只覆盖根因 1-3（type 信息丢失）；实施中发现根因 4 是更深的问题：即便 type 信息正确，跨 namespace 的同包 class 引用也会因 namespace 错位而 runtime 失败。一并修复
- generic param 名 (K/V) 已经能通过 `Z42GenericParamType gp => gp.Name` 序列化为 "K"/"V"
- 不支持嵌套深度限制 — 用栈式 `<>` 配对解析（与 IsTypeAnnotatedVarDecl 思路一致）
- 验证：dotnet test 725/725 + test-vm 200/200（100 interp + 100 jit）全绿
