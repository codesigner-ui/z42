# Tasks: L3-G4f 源码级泛型容器（ArrayList<T> + HashMap<K,V>）

> 状态：🟢 已完成（Scope 裁剪：HashMap 延到 L3-G4g）| 创建：2026-04-22 | 完成：2026-04-22 | 类型：stdlib+test

**变更说明**：在 stdlib 新增两个真正的源码级泛型容器：
- `Std.Collections.ArrayList<T>`：List 的源码替代，Add/Count/indexer/Contains/RemoveAt/Clear/Insert/IndexOf
- `Std.Collections.HashMap<K,V>`：Dictionary 的源码替代（桶 + 线性探测），Set/Get/ContainsKey/Remove/Count

与 pseudo-class `List<T>` / `Dictionary<K,V>` **并存**（不替换）。用户可选用。pseudo-class
真正移除需要先做 foreach iterator 协议（L3-G4g 或后期），本次不碰。

**原因**：最终决定是 pseudo-class 短期不能移除（foreach 依赖）。但写出真正的 stdlib
源码版本对验证泛型机制（L3-G1 ~ L3-G4e）的完整性极有价值，并为未来替换做好基础。

## 进度概览
- [x] 阶段 1: 写 `Std.Collections.ArrayList<T>`（含 indexer）✅
- [ ] 阶段 2: 写 `Std.Collections.HashMap<K,V>` — **延到 L3-G4g**（跨命名空间约束解析 gap）
- [x] 阶段 3: Golden 测试 `run/80_stdlib_arraylist` ✅
- [ ] 阶段 4: HashMap golden — 同延后
- [x] 阶段 5: 文档 + GREEN ✅

## 阶段 1: ArrayList<T> ✅

- [x] 1.1 新文件 `src/libraries/z42.collections/src/ArrayList.z42`
- [x] 1.2 实现字段：`T[] items`, `int count`, `int capacity`
- [x] 1.3 方法：Add, Count, Clear, RemoveAt, Insert（Contains/IndexOf **延后** — 见 Scope 裁剪）
- [x] 1.4 索引器：`public T this[int i] { get; set; }`
- [x] 1.5 扩容：Grow() 指数增长
- [x] 1.6 `./scripts/build-stdlib.sh` 成功编译

## 阶段 2: HashMap<K,V>

- [ ] 2.1 新文件 `src/libraries/z42.collections/src/HashMap.z42`
- [ ] 2.2 桶式实现：`K[] keys`, `V[] values`, `bool[] occupied` + 线性探测
- [ ] 2.3 方法：Set, Get, ContainsKey, Remove, Count, Clear
- [ ] 2.4 索引器：`public V this[K key] { get; set; }`
- [ ] 2.5 GetHashCode 取模定位；Equals 判相等
- [ ] 2.6 扩容：Resize() 负载因子 > 0.75 时翻倍
- [ ] 2.7 `./scripts/build-stdlib.sh` 成功编译

## 阶段 3: ArrayList 测试 ✅

- [x] 3.1 Golden `run/80_stdlib_arraylist`：Add / indexer get+set / RemoveAt / Insert / Clear / Count / IsEmpty
- [x] 3.2 Int + string 两种 T 都测
- [x] 3.3 扩容路径（push >capacity 元素）

## 阶段 4: HashMap 测试

- [ ] 4.1 Golden `run/81_stdlib_hashmap`：完整 API 覆盖（Set/Get/ContainsKey/Remove/Count）
- [ ] 4.2 string-int / int-string 两种键值组合
- [ ] 4.3 冲突处理（hash collision → 线性探测）
- [ ] 4.4 扩容路径

## 阶段 5: 文档 + GREEN ✅

- [x] 5.1 `docs/design/generics.md`：L3-G4f 小节
- [x] 5.2 `docs/roadmap.md`：L3-G4f ✅（ArrayList 部分）；新增 L3-G4g（HashMap + 跨命名空间约束）/ L3-G4h（foreach iterator）占位
- [x] 5.3 `dotnet build` + `dotnet test` 524/524 ✅; `test-vm.sh` 154/154 ✅（77 interp + 77 jit）

## 备注

- **零编译器改动、零 VM 改动**：纯 stdlib 源码新增
- **pseudo-class 不替换**：`List<T>` / `Dictionary<K,V>` 继续走 pseudo-class 路径（foreach 依赖）
- **命名选择**：ArrayList / HashMap 是公认名字（Java/Rust），避免与 pseudo-class `List` / `Dictionary` 冲突
- **HashMap K 约束**：key 需要实现 IEquatable / 有 GetHashCode；L3-G4b 已让 primitive 满足；user 类需要自己实现（或继承 Object 默认）

## Scope 外发现记录区

- **跨命名空间约束解析 gap**：从 `Std.Collections` 命名空间的类引用 `Std.IEquatable<T>` 约束失败：
  - 短名 `IEquatable` 在跨模块作用域里未被解析（TypeChecker 报 "unconstrained type parameter T"）
  - 长名 `Std.IEquatable<T>` 未被 Parser 接受（`.` 不在类型位置允许）
  - 结果：ArrayList 的 `Contains` / `IndexOf`（需 T.Equals）无法声明；HashMap（需 IEquatable + IHash）整体延后
  - **落地处理**：单独 L3-G4g 处理（跨命名空间解析修复 + HashMap + Contains/IndexOf 补齐）
