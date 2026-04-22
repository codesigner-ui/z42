# Tasks: L3-G2.5 chain — `where T: I<U>, U: J` 跨参数约束链校验

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 接口约束类型参数从"仅名字匹配"升级为"名字 + TypeArgs 替换校验"。
`where T: IEquatable<U>` 在实例化 `Foo<int, string>` 时会把 U 替换为 string 再校验
`int` 是否满足 `IEquatable<string>`（不满足 → 编译期报错）。
**原因：** G3d 完成后 TSIG 能传约束，但接口约束 args 仍被忽略；跨参数链接的错误只在
运行时 VCall 或完全不报。补齐到编译期。
**文档影响：** `docs/design/generics.md`（G2.5 chain 记录）、`docs/roadmap.md`。

## 任务

- [x] 1.1 `Z42InterfaceType` 新增 `TypeArgs: IReadOnlyList<Z42Type>?`
       （默认 null，向后兼容所有现存构造器）
- [x] 1.2 `SymbolTable.ResolveGenericType`：遇到接口 + 泛型实参时构造带 TypeArgs
       的 `Z42InterfaceType`；无 args 时保留既有实例
- [x] 1.3 `TypeChecker.ValidateGenericConstraints`：在 interface 检查前调用
       `SubstituteInterfaceTypeArgs` 把 U 等 type-param ref 替换为具体实参
- [x] 1.4 `TypeSatisfiesInterface` 分流：
       - `PrimitiveSatisfies`: primitive 只满足 `I<Self>`（int 实现 IEquatable<int>）
       - `GenericParamSatisfies`: 按 name + args 双重匹配
       - 类 / interface / Instantiated：维持名字匹配（classes 尚未跟踪 interface args）
- [x] 1.5 Unit test `TypeCheckerTests`：3 个 ChainConstraint 测试
       （self-referential、cross-param mismatch、cross-param match）
- [x] 1.6 GREEN：533 C# 测试 + 160 VM 测试全绿

## 备注

- **Class 侧仍是名字匹配**：`class MyClass: IEquatable<string>` + `where T: IEquatable<U>`
  (U=int) 当前还会通过校验（因为 MyClass.Interfaces 只存名字）。后续若要完整
  class 级检查，需要在 `Z42ClassType.Interfaces` 里记录 `Z42InterfaceType` 而不是 `string`
  —— 独立迭代，非本次 scope
- **TSIG 不扩展**：G3d 存的是 interface name list（IEquatable, IComparable）；约束
  rehydrate 时用 `_symbols.Interfaces` 查回 Z42InterfaceType，不携带 args。跨 zpkg 的
  `where T: IEquatable<U>`（U 为泛型参数）目前也靠 substitution 做；接口的 args
  重建仅在本地 decl 正确 —— 跨 zpkg 若写了 `where T: IEquatable<U>` 的本地代码效果相同
- 变型标注（`<in T>` / `<out T>`）未涉及 —— 见 roadmap L3-G2.5 剩余项
