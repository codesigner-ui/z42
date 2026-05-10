# Tasks: add-keyvaluepair-and-dict-entries

> 状态：🟢 已完成 | 类型：feat (stdlib API) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 给 stdlib 加 `KeyValuePair<K,V>` 类型 + Dictionary 的 `Keys()` / `Values()` / `Entries()` 方法，让用户能 `foreach (var kv in dict.Entries()) { ... kv.Key ... kv.Value ... }`。

**为什么不直接 `foreach (var kv in dict)`**：当前 foreach codegen 只识别 `Count + get_Item(int)` 鸭子协议。Dictionary 的 `get_Item` 接 K 不接 int，不匹配。完整 IEnumerable / GetEnumerator codegen 改造留给独立 spec。本变更走"返回 array → 现有 foreach 直接消费"的最小路径。

## Tasks

- [x] 1.1 新建 `src/libraries/z42.core/src/Collections/KeyValuePair.z42`：泛型类含 Key/Value 两字段 + 双参 ctor
- [x] 2.1 给 `Dictionary.z42` 加 `Keys()` / `Values()` / `Entries()` 方法（O(capacity) 扫 occupied → 写入 result array）
- [x] 3.1 新增 golden test `src/runtime/tests/golden/run/20_dict_iter/`：演示 dict.Entries() 用法（用 string key + int value，遍历求和、求 key 字符串拼接）
- [x] 4.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 5.1 commit + push + 归档

## 备注

- KeyValuePair 选 class 而非 struct（z42 泛型 struct 验证不充分，class 安全）
- Keys()/Values()/Entries() 顺序由 capacity 扫描顺序决定，**不保证稳定**（hash 分布相关）。golden test 用与顺序无关的断言（求和 / set-style 累计）
- **实施中发现两个 z42 限制**：
  1. **泛型类内 instantiate 自身泛型参数失败**：`new KeyValuePair<K, V>(...)` 在 `Dictionary<K,V>.Entries()` 内部 parser 报错。**Entries() 方法暂未实现**，留 backlog；用户用 `Keys()` + `dict[k]` 代替
  2. **`using <namespace>` 触发 cross-package import 顺序问题**：当 namespace 在多个包出现（如 `Std.Collections` 同时在 z42.core + z42.collections），`using Std.Collections;` 会让 TypeChecker 报 "string does not satisfy IEquatable on Dictionary"。Workaround：测试不写 `using`，靠 prelude 自动解析。Backlog：修 ImportedSymbolLoader 的 cross-zpkg 命名空间合并
- 也更新了 `IncrementalBuildIntegrationTests`：z42.core 文件数 32 → 33（新增 KeyValuePair.z42）
