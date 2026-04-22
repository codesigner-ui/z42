# Tasks: L3-G4h Step 3 — Pseudo-class List/Dict 正式退场

> 状态：🟢 已完成 | 完成：2026-04-22

**变更说明：** 移除编译器 List/Dictionary pseudo-class 快路径，`new List<T>()` 现在
路由到纯源码级 `Std.Collections.List<T>`（前身 ArrayList）；Dictionary 同理对应
`Std.Collections.Dictionary<K,V>`（前身 HashMap）。
**原因：** 完成 L3-G4h "完整实现 List/Dictionary" 目标 —— 用户可继承、增强、泛化
stdlib 容器，不再受编译器硬编码限制。
**文档影响：** `docs/design/generics.md`（step 3 记录）、`docs/roadmap.md`（G4h 标记 ✅）。

## 任务

- [x] 3.1 `List.z42`：整合 ArrayList 的实现 + `Sort` / `Reverse` / `Remove`；
       `Count` 从 `Count()` 方法改为 `public int Count` 字段
- [x] 3.2 `Dictionary.z42`：整合 HashMap 实现；`Count` 改为 public 字段
- [x] 3.3 删除 `ArrayList.z42` / `HashMap.z42` 源文件（并入 List/Dictionary）
- [x] 3.4 `SymbolTable.ResolveGenericType`：删除 List/Dictionary pseudo 映射
- [x] 3.5 `FunctionEmitterExprs.EmitBoundNew`：删除 `__list_new` / `__dict_new` case
- [x] 3.6 `IsBuiltinCollectionType`（两处）收窄到 Array/StringBuilder
- [x] 3.7 `ResolveBuiltinMethod`：仅保留 StringBuilder `__sb_*` 映射
- [x] 3.8 `FunctionEmitterStmts.ClassIterTarget`：支持 `Count` 字段（而非仅方法）
       作为 foreach 长度源，并移除 pseudo-class 名称硬排除
- [x] 3.9 测试迁移：80 → List<T>；81 → Dictionary<K,V>；83 → List；
       18/20/40 零改动直接跑通源码版本
- [x] 3.10 GREEN：527 C# 测试 + 160 VM 测试（interp+jit）全绿

## 备注

- VM 侧 `__list_*` / `__dict_*` builtins 保留未删 —— 编译器不再发射，可在未来
  独立 refactor 中清理
- `Sort` 为插入排序（O(n^2)），可升级到归并排序的单独迭代
- pseudo-class 移除暴露 JIT 不完整支持 imported 类 VCall 的 gap —— 解决办法是
  DepIndex 通过 ambiguous 移除后的 fallback，JIT 受 `List.Add` / `Dictionary.Set`
  的 CallInstr 正确分发
