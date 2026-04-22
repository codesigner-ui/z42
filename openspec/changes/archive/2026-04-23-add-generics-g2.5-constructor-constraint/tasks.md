# Tasks: L3-G2.5 构造器约束 `where T: new()`

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增构造器约束 `where T: new()`，要求类型实参有无参构造器。
**原因：** 泛型工厂 / 容器场景需要（`Factory<T> where T: new() { T Create() { ... } }`）；
是 L3-G2.5 约束范式补全的高优先级项。
**Scope 限制**：本次仅实现**约束校验**（编译期拒绝无 no-arg ctor 的实参）；
泛型 body 内 `new T()` 实例化**依赖 L3-R 运行时 type_args 传递**，单独迭代。
**文档影响**：`docs/design/generics.md`（约束语法决策、new() 约束语义）、
`docs/roadmap.md`（G2.5 状态）、ir.md（zbc 约束 flags 位）。

## 任务

- [x] 1.1 AST：`GenericConstraintKind` 新增 `Constructor = 1 << 2`
- [x] 1.2 Parser `ParseOneConstraint`：识别 `new ( )` token 序列 → 设置 Constructor flag
- [x] 1.3 `GenericConstraintBundle`：新增 `RequiresConstructor` 字段
- [x] 1.4 `TypeChecker.ResolveWhereConstraints`：将 kind flag 映射到 bundle
- [x] 1.5 `ValidateGenericConstraints`：`HasNoArgConstructor` 校验路径
- [x] 1.6 zbc metadata flags：新增 bit `0x10` for RequiresConstructor
- [x] 1.7 TSIG flags：同步新增 bit `0x10`
- [x] 1.8 `ExportedTypeParamConstraint` + extractor + loader 增加字段
- [x] 1.9 Unit tests：5 个 CtorConstraint 用例
       （class with ctor ✅ / class without ✘ / primitive ✅ / interface ✘ / combined ✅）
- [x] 1.10 GREEN：538 C# + 160 VM 全绿

## 附带清理（用户要求）

- [x] 删除 `IrModule.cs` 全部 `JsonPropertyName` / `JsonIgnore` / `JsonConverter`（35 处）
- [x] 移除 `using System.Text.Json.Serialization`
- [x] 删除 `SingleFileCompiler.Run` 的 `JsonSerializerOptions` 参数（未使用）
- [x] 删除 `Program.cs` 的 `jsonOptions` 定义 + 相关 usings

## 备注

- `new T()` 在 body 内使用当前仍然会失败（T 被擦除，VM ObjNew 找不到类）—— 约束先落地，
  body 支持等 L3-R type_args 传递机制
- zbc 版本号无需提升（flags 字节新位向后兼容：旧位全 0 不触发新验证）
- 与 class/struct 互斥：`class + new()` OK（reference 类也能有 ctor）；`struct + new()` OK
