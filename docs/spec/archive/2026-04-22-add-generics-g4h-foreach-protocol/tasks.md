# Tasks: L3-G4h Step 2 — foreach duck-typed 迭代协议

> 状态：🟢 已完成 | 完成：2026-04-22

**变更说明：** `foreach (var e in c)` 支持类型的鸭子类型迭代 —— 类上有
`int Count()` + `T get_Item(int)` 即可被 foreach 迭代，lower 为索引驱动 for 循环。
**原因：** 为 step 3 移除 pseudo-class List/Dictionary 做准备；user-source ArrayList /
HashMap / 自定义容器也能直接用 foreach，无需 VM 特殊分发或 IEnumerable 接口协议。
**文档影响：** `docs/design/language-overview.md`（foreach 鸭子类型语义）、
`docs/design/generics.md`（G4h step2 记录）、`docs/roadmap.md`。

## 任务

- [x] 2.1 `TypeChecker.cs`: `ElemTypeOf` 识别 class / InstantiatedType 的 `get_Item` 返回类型（substituted）
- [x] 2.2 `FunctionEmitterStmts.cs`: `EmitBoundForeach` 对 class 容器 emit `VCall Count` + `VCall get_Item`
- [x] 2.3 Pseudo-class List/Dictionary 仍走原数组路径（ClassIterTarget 排除这两个）
- [x] 2.4 Golden test `83_foreach_user_class/`: 自定义 `Ring<T>` + `ArrayList<T>`，覆盖 break/continue
- [x] 2.5 文档：language-overview 增补 foreach 鸭子协议；generics.md 记录 step2
- [x] 2.6 GREEN: dotnet build / cargo build / dotnet test / test-vm.sh 全绿

## 备注

- 未引入 `IEnumerable<T>` / `IEnumerator<T>` 接口 —— 保留以 interface 路径做未来扩展空间
- 暂不支持无大括号的 `foreach (var x in r) stmt;`（parser 限制，所有 G4h 测试都用 `{}` 块）
- Pseudo-class List/Dict foreach 仍然走 Array/Map fast path，等 step 3 一并移除
