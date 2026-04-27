# Tasks: fix-using-prelude-include

> 状态：🟢 已完成 | 类型：fix (typecheck) | 创建：2026-04-27 | 完成：2026-04-28

**变更说明：** 当源码有显式 `using Ns;` 声明时，TypeChecker 也应自动包含 `Std`（prelude）以避免 `IEquatable` / `Object` / `Exception` 等基础类型不可见的假阳性错误。

**根因：** [ImportedSymbolLoader.Load](src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs#L34) 严格按 `usings` 过滤 namespace。如果用户写 `using Std.Collections;`，loader 只加载 `Std.Collections` 命名空间，把 `Std` 命名空间的 IEquatable / Object 等过滤掉。然后 TypeChecker 验证 `Dictionary<K,V> where K: IEquatable<K>` 约束时找不到 IEquatable，报 "string does not satisfy IEquatable on Dictionary"。

**复现：** [20_dict_iter spec 备注](src/runtime/tests/golden/run/20_dict_iter/source.z42) 提到的现象。当时用 workaround：不写 `using Std.Collections;`，靠 GoldenTests 的 fallback "load all namespaces"。

**修复：** `Std` 是隐式 prelude（[philosophy.md](docs/design/philosophy.md) §1 / [stdlib.md "z42.core auto-load"](docs/design/stdlib.md)），无论用户写什么 using，都应可见。在 `Load` 内部把 `Std` 强制加入 `allowedNs`。

## Tasks

- [x] 1.1 `ImportedSymbolLoader.Load`：进入 method 后立即 `allowedNs.Add("Std")`，确保 prelude 命名空间永远命中
- [x] 2.1 加 golden test `21_using_collections/`：源码用 `using Std.Collections;` + `new Dictionary<string, int>()`（验证 fix）
- [x] 3.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 4.1 commit + push + 归档

## 备注

- 不影响只用单包内类型的代码（多加 Std 命名空间不会破坏什么）
- 不影响 PackageCompiler，它本来就 load all namespaces
- 与 docs/design/stdlib.md "z42.core 是隐式 prelude" 描述对齐
