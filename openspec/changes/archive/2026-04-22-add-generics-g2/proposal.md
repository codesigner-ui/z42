# Proposal: L3-G2 泛型约束（interface constraints）

## Why

L3-G1 完成后，泛型函数/类无法在 T 上调用任何方法（因 T 无已知行为），泛型实用性受限。必须引入约束系统，才能写出：

```z42
T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}
```

选定 Rust 风格 `+` 多约束（见 `docs/design/generics.md` 及用户记忆），Parser/TypeCheck 同时落地。

## What Changes

- **语法**：`where T: I [+ J]* [, K: I2]*` 子句，放在 FunctionDecl/ClassDecl/InterfaceDecl 签名尾部、方法体前
- **AST**：新增 `WhereClause` + `GenericConstraint(TypeParam, Constraints)`，挂到 FunctionDecl/ClassDecl/InterfaceDecl
- **TypeCheck**：
  - `Z42GenericParamType.Constraints: List<Z42InterfaceType>` 携带约束
  - 泛型函数/类体内：`t.Method(...)` 允许查找约束 interface 的方法
  - 调用点 `Max<MyClass>(...)`：验证 `MyClass` 实现所有约束接口
- **IrGen**：无改动（代码共享延续），不向二进制写入 constraint 元数据（L3-G3 反射需要时再加）
- **VM**：无改动（运行时沿用 VCall 动态分发，已支持接口方法）

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/compiler/z42.Syntax/Ast.cs` | 新增 | `WhereClause`, `GenericConstraint` records；FunctionDecl/ClassDecl/InterfaceDecl 追加 `WhereClause?` 字段 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | 新增 | `ParseWhereClause` 返回 `WhereClause?` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | 修改 | Function/Class/Interface parse 挂载 where 子句 |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | 修改 | `Z42GenericParamType` 新增 `Constraints` |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector*.cs` | 修改 | 解析 where 子句 → `Z42GenericParamType` 附加约束 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | 修改 | `t.Method(...)` 查找约束 interface 方法；`Max<T>(...)` 调用点校验实现 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` | 修改 | PushTypeParams 接受约束映射 |
| `src/libraries/z42.core/src/IComparable.z42` | 修改 | 取消注释，启用 `interface IComparable<T>` |
| `src/libraries/z42.core/src/IEquatable.z42` | 修改 | 取消注释（若已注释）或确认启用 |
| `src/compiler/z42.Tests/TypeCheckerTests.cs` | 新增 | 5+ 个用例：单/多约束、调用点校验、未实现错误、约束方法调用 |
| `src/runtime/tests/golden/run/70_generic_constraints/` | 新增 | Golden test：用户类实现 IComparable + 泛型 Max |
| `docs/design/generics.md` | 修改 | L3-G2 状态更新；语法细节完善 |
| `docs/roadmap.md` | 修改 | L3-G 进度表 G2 → ✅ |

## Out of Scope（本次不做，但已列入后续阶段）

- **primitive 类型（int/string）隐式实现 interface**：留到 L3-G4 stdlib 泛型化；本次 `Max<int>` 不可用
- **关联类型** `type Output; Output=T`：L3-G3
- **基础约束** `where T: class / struct / new()`：本次不做，仅 interface 约束
- **Trait 约束**（替代 interface）：L3 后期
- **约束元数据写入 zbc + VM 侧校验 + 反射接口**：**L3-G3 必须补齐**（见 design.md Future Work）。
  - 二进制格式扩展（SIGS/TYPE section 加 constraint 字段）
  - VM 加载阶段读取约束并在 ObjNew / 泛型 Call 时校验（untrusted zbc）
  - `type.Constraints` / `t is IComparable<T>` 反射能力
  - 跨 zpkg 依赖签名也需带约束字段（TSIG section 扩展）

## Open Questions

- [ ] 约束语法冲突：`where T: I + J` 和表达式 `a + b` 的 lookahead？Parser 解析 where 在 `{` 之前，应不冲突，设计里确认。
- [ ] 约束方法调用是 Call 还是 VCall？当前 VCall 需要实际 vtable，泛型参数 T 的实际类型运行时才知 → 必须 VCall。
