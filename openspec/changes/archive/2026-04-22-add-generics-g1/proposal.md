# Proposal: L3-G1 泛型基础（泛型函数 + 泛型类，无约束）

## Why

泛型是 L3 阶段的核心基石，解锁 Lambda（Func<T>）、Result<T,E>、LINQ 等后续特性。当前 List<T>/Dict<K,V> 靠 pseudo-class 硬编码，无法扩展用户自定义泛型类型。

## What Changes

1. **Parser**：解析 `<T>` / `<K,V>` 类型参数列表；解析泛型调用 `Foo<int>(...)`
2. **AST**：FunctionDecl/ClassDecl/InterfaceDecl 新增 `TypeParams` 字段；新增 `GenericType` TypeExpr
3. **TypeChecker**：泛型参数作用域管理；调用时类型参数替换和验证
4. **IrGen**：生成共享代码 + type_params 元数据
5. **VM**：TypeDesc 扩展 type_params/type_args；泛型类实例化时创建实例化 TypeDesc

## Scope

| 文件/模块 | 变更类型 |
|-----------|---------|
| `z42.Syntax/Parser/Ast.cs` | 修改：AST 节点增加 TypeParams |
| `z42.Syntax/Parser/TopLevelParser*.cs` | 修改：解析 `<T>` |
| `z42.Syntax/Parser/TypeParser.cs` | 修改：解析 `GenericType` |
| `z42.Semantics/TypeCheck/TypeChecker*.cs` | 修改：泛型参数作用域 + 替换 |
| `z42.Semantics/Codegen/FunctionEmitter.cs` | 修改：type_params 元数据 |
| `z42.IR/IrModule.cs` | 修改：IrFunction 增加 type_params |
| `src/runtime/src/metadata/types.rs` | 修改：TypeDesc 扩展 |
| golden tests | 新增 |

## Out of Scope

- `where` 约束（L3-G2）
- 关联类型（L3-G3）
- 泛型标准库替换 pseudo-class（L3-G4）
- 泛型方法的类型推断（后续迭代）
