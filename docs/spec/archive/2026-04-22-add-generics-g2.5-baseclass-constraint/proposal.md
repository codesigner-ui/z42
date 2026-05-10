# Proposal: L3-G2.5 基类约束（`where T: BaseClass`）

## Why

L3-G2 仅支持 interface 约束，无法表达"T 必须继承自某个类"。常见需求：

```z42
class Component { public string Name; }

void Attach<T>(T component) where T: Component {
    Console.WriteLine(component.Name);  // 访问基类字段
}
```

本变更补齐 Rust 类型 bound / C# `where T: Class` 的通用范式，与 interface 约束形成对称能力。

## What Changes

- **语法**：`where T: BaseClass [+ I + J]*` — 基类与 interface 混用，基类须为第一项（C# 惯例）
- **AST**：`GenericConstraint.Constraints: List<TypeExpr>` 已就绪；无结构变更
- **TypeCheck**：
  - `Z42GenericParamType.Constraints` 扩展语义：`List<Z42Type>`（union: Z42InterfaceType 或 Z42ClassType）
  - 约束作为基类 → 成员查找延伸到类字段 / 方法 / 静态方法
  - 调用点校验：`F<MyClass>(...)` 中 MyClass 必须是基类的子类（或等于基类）
  - 方法调用 dispatch：约束为类 → 生成 VCall（类方法可能 virtual）
- **IrGen / VM**：无改动
- **stdlib**：无改动

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | 修改 | `Z42GenericParamType.Constraints` 类型改为 `IReadOnlyList<Z42Type>?` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | 修改 | `ResolveWhereConstraints` 放宽为接受 interface 或 class；`ValidateGenericConstraints`/`TypeSatisfiesInterface` 扩展为 `TypeSatisfiesConstraint`（类用 IsSubclassOf） |
| `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` | 修改 | 活动约束查询返回 `IReadOnlyList<Z42Type>` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` | 修改 | 受约束 T 的方法调用：先查 interface，再查 class 基类方法 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | 修改 | 同上 for BindMemberExpr（字段访问） |
| `src/compiler/z42.Tests/TypeCheckerTests.cs` | 新增 | 4-5 用例（字段访问、方法调用、子类调用点、非子类错误、基类 + interface 组合） |
| `src/runtime/tests/golden/run/71_generic_baseclass/` | 新增 | Golden：`Animal` 基类 + `Dog` 子类 + `void Describe<T>(T x) where T: Animal` |
| `docs/design/generics.md` | 修改 | L3-G2.5 小节 |
| `docs/roadmap.md` | 修改 | L3-G2.5 状态表（baseclass 子项） |

## Out of Scope（本次不做）

- **`where T: new()` 构造器约束**：`new T()` 泛型体内实例化需要 VM 侧的运行时类型参数传递；代码共享策略下无法在纯编译期解决。**延后至 L3-G3a**（`type_args` 填充到 TypeDesc + VM loader 扩展）同步做。
- `where T: class` / `struct` / `notnull`：纯 flag，排期 G2.5 后续子迭代
- 约束元数据写入 zbc：统一在 L3-G3a 二进制格式变更时做
- 基类虚方法调用的 `base.Method()` 在泛型体内：不需要，生成 VCall 就地走 T 的实际类型 vtable

## Open Questions

- [ ] `where T: Foo + IBar` 中基类可不可以是第二项？**决定**：基类须是第一项，否则报错（C# 惯例，简化解析）
- [ ] 多基类约束（`where T: A + B`，A 和 B 都是类）：不允许，报错（单继承模型）
- [ ] 约束基类 + 调用点显式参数（`Attach<Dog>(d)`）：复用现有显式 type arg 校验路径
