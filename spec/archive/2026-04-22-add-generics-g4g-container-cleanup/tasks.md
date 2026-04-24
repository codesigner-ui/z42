# Tasks: L3-G4g 泛型容器清理 + 跨命名空间约束 gap 修复

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22 | 类型：compiler+stdlib

**变更说明**：清理现有泛型容器实现，让 `List<T>` / `Dictionary<K,V>` 之类
容器有合适的源码版本（`ArrayList<T>` / `HashMap<K,V>`），同时修复 L3-G4f 遗留
的跨命名空间约束解析 gap。删除 `docs/review.md`（迭代完毕）。

## 完成项

### 编译器基础设施修复
- [x] `PackageCompiler.BuildLibsDirs`：向上走查 `artifacts/z42/libs` 目录，stdlib 互编时能看到兄弟包
- [x] `ExportedTypeExtractor.ExtractClasses`：不再把 imported 类重新导出到 TSIG（之前导致 `new HashMap<...>()` 被路由到 `Std.Text.HashMap` 等错误命名空间）
- [x] `Z42Type.IsAssignableTo`：`T[]` 跨约束态等价按元素名匹配（字段存 T 无约束，方法体内 T 带 where-clause 约束 → 记录相等失败；改为名称相等时允许）

### stdlib
- [x] `IEquatable<T>` 加 `GetHashCode()`：配对 Equals 的哈希能力，HashMap 可依赖它
- [x] `Std.Collections.ArrayList<T>` 补齐 `Contains` + `IndexOf`（通过 `where T: IEquatable<T>`）
- [x] `Std.Collections.HashMap<K,V>` 新上线：开放寻址 + 线性探测；indexer / Set / Get / ContainsKey / Remove / Clear / Count / Grow
- [x] Stub 文件清理：`List.z42` / `Dictionary.z42` / `HashSet.z42` 替换为简短注释指向实际实现

### 测试
- [x] Golden `run/80_stdlib_arraylist` 扩展：Contains / IndexOf 正负用例
- [x] Golden `run/81_stdlib_hashmap`：string→int / int→string / 20 项 grow 场景
- [x] dotnet test 525/525 ✅; cargo test --lib 53/53 ✅; test-vm.sh 156/156 ✅（78 interp + 78 jit）

### 其他
- [x] 删除 `docs/review.md`（评审已落地）
- [x] `docs/design/generics.md` / `docs/roadmap.md` 同步

## Scope 外发现（记录给 L3-G4h）

- **`&&` / `||` 不短路**：z42 编译为 IR 层的 `and` / `or` 会先求两边再合并。`HashMap.FindSlot` 初版 `occupied[s] && keys[s].Equals(k)` 会在 occupied=false 时仍 VCall `Equals`（对未初始化 Null 报错）。临时用嵌套 if 规避；真短路 desugar 留给 L3-G4h
- **bool[] 默认初始化为 Null**：VM `new bool[N]` 不是零初始化 — HashMap ctor 手动遍历 `occupied[i] = false`；这是语义 gap，L3-G4h 考虑统一

## 备注

- pseudo-class `List<T>` / `Dictionary<K,V>` 继续保留（L1/L2 backward compat + foreach 支持）
- 真正移除 pseudo-class 等 L3-G4h：foreach iterator 协议 + `&&`/`||` 短路
